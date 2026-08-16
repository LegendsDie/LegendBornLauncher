using CmlLib.Core;
using LegendBorn.Services;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LegendBorn.Launching;

/// <summary>
/// NeoForge installer implementation backed by the authoritative launcher catalog.
/// Original installer bytes are SHA-256 verified before Java starts. The JAR is never mutated.
/// NeoForge-hosted Maven artifacts declared by the installer are prefetched into the temporary
/// Minecraft library directory from the selected catalog mirror and verified by their published
/// SHA-1 before the original installer is executed.
/// </summary>
public sealed class LoaderInstaller
{
    private const long MaxInstallerBytes = 100L * 1024 * 1024;
    private const long MaxLibraryArtifactBytes = 200L * 1024 * 1024;
    private const long MaxMetadataEntryBytes = 4L * 1024 * 1024;
    private const int OutputCapChars = 512 * 1024;
    private const int NeoForgePrefetchParallelism = 6;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OfficialDownloadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InstallOverallTimeout = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan JavaProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly MinecraftPath _path;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record InstallerArtifact(string Path, string Sha1, long Size, string Url);

    public LoaderInstaller(MinecraftPath path, HttpClient http, Action<string>? log = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;
    }

    public async Task<string> EnsureInstalledAsync(string minecraftVersion,string loaderType,string loaderVersion,string installerUrl,CancellationToken ct)
    {
        minecraftVersion=(minecraftVersion??"").Trim(); loaderType=(loaderType??"vanilla").Trim().ToLowerInvariant(); loaderVersion=(loaderVersion??"").Trim(); installerUrl=(installerUrl??"").Trim();
        if(loaderType=="vanilla") return minecraftVersion;
        if(loaderType!="neoforge") throw new NotSupportedException($"Loader '{loaderType}' не поддерживается.");
        if(minecraftVersion.Length==0||loaderVersion.Length==0) throw new InvalidOperationException("NeoForge version contract incomplete.");
        if(!NeoForgeDistributionBootstrap.TryResolve(loaderVersion,out var distribution)) throw new InvalidOperationException($"Для NeoForge {loaderVersion} отсутствует доверенный distribution contract.");
        if(!NeoForgeDistributionBootstrap.IsSha256(distribution.InstallerSha256)) throw new InvalidOperationException("NeoForge installer SHA-256 отсутствует или повреждён.");
        var compatibilityUrl=NeoForgeDistributionBootstrap.NormalizeHttpsUrl(installerUrl);
        if(compatibilityUrl.Length>0&&!distribution.InstallerMirrors.Contains(compatibilityUrl,StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("loader.installerUrl не входит в catalog installerMirrors.");
        var expectedId=$"{minecraftVersion}-neoforge-{loaderVersion}";
        var gate=InstallLocks.GetOrAdd(expectedId,static _=>new SemaphoreSlim(1,1)); await gate.WaitAsync(ct).ConfigureAwait(false);
        try{
            if(IsVersionPresent(expectedId)){_log?.Invoke($"NeoForge: уже установлен -> {expectedId}");return expectedId;}
            var installerPath=await DownloadVerifiedInstallerAsync(minecraftVersion,distribution,ct).ConfigureAwait(false);
            RequireInstallerHash(installerPath,distribution.InstallerSha256); ValidateInstallerMetadata(installerPath,loaderVersion);
            var installed=await InstallOriginalJarAsync(installerPath,minecraftVersion,loaderVersion,expectedId,distribution,ct).ConfigureAwait(false);
            return installed??throw new InvalidOperationException("NeoForge installer завершился, но version JSON не найден.");
        }finally{gate.Release();}
    }

    private async Task<string> DownloadVerifiedInstallerAsync(string minecraftVersion,NeoForgeDistributionSpec distribution,CancellationToken ct)
    {
        var root=_path.BasePath??throw new InvalidOperationException("MinecraftPath.BasePath пустой.");
        var dir=Path.Combine(root,"launcher","installers","neoforge",minecraftVersion,distribution.LoaderVersion); Directory.CreateDirectory(dir);
        var finalPath=Path.Combine(dir,$"neoforge-{distribution.LoaderVersion}-installer.jar");
        if(File.Exists(finalPath)){try{RequireInstallerHash(finalPath,distribution.InstallerSha256);ValidateInstallerMetadata(finalPath,distribution.LoaderVersion);_log?.Invoke("NeoForge installer: использую проверенный локальный кеш.");return finalPath;}catch{TryDelete(finalPath);}}
        Exception? last=null;
        foreach(var candidate in distribution.InstallerMirrors.Distinct(StringComparer.OrdinalIgnoreCase)){
            ct.ThrowIfCancellationRequested(); var normalized=NeoForgeDistributionBootstrap.NormalizeHttpsUrl(candidate); if(!Uri.TryCreate(normalized,UriKind.Absolute,out var uri)) continue;
            var temp=finalPath+".tmp"; TryDelete(temp);
            try{
                var source=NeoForgeDistributionBootstrap.DescribeSource(normalized); _log?.Invoke($"NeoForge installer: {source}"); if(source=="BMCLAPI") _log?.Invoke("Источник загрузки: BMCLAPI");
                await DownloadFileAsync(uri,temp,MaxInstallerBytes,null,ct).ConfigureAwait(false); RequireInstallerHash(temp,distribution.InstallerSha256); ValidateInstallerMetadata(temp,distribution.LoaderVersion); File.Move(temp,finalPath,true); return finalPath;
            }catch(OperationCanceledException) when(ct.IsCancellationRequested){TryDelete(temp);throw;}catch(Exception ex){last=ex;TryDelete(temp);_log?.Invoke($"NeoForge installer mirror failed: {ex.Message}");}
        }
        throw new InvalidOperationException("Ни одно installer mirror не отдало JAR с ожидаемым SHA-256.",last);
    }

    private async Task DownloadFileAsync(Uri uri,string path,long maxBytes,long? expectedSize,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct); linked.CancelAfter(uri.Host.Equals("maven.neoforged.net",StringComparison.OrdinalIgnoreCase)?OfficialDownloadTimeout:DownloadTimeout);
        using var request=new HttpRequestMessage(HttpMethod.Get,uri); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,linked.Token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is long contentLength){if(contentLength<=0||contentLength>maxBytes) throw new InvalidOperationException($"Некорректный размер файла: {contentLength}");if(expectedSize is >0&&contentLength!=expectedSize.Value) throw new InvalidOperationException($"Размер файла не совпал: expected={expectedSize.Value}, actual={contentLength}");}
        var mediaType=response.Content.Headers.ContentType?.MediaType??""; if(mediaType.Contains("text/html",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Вместо бинарного файла получена HTML-страница.");
        await using var input=await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false); await using var output=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None,128*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
        byte[]? buffer=null; try{buffer=ArrayPool<byte>.Shared.Rent(128*1024);long total=0;while(true){var read=await input.ReadAsync(buffer.AsMemory(0,buffer.Length),linked.Token).ConfigureAwait(false);if(read<=0)break;total+=read;if(total>maxBytes)throw new InvalidOperationException($"Файл превышает безопасный размер {maxBytes} bytes.");await output.WriteAsync(buffer.AsMemory(0,read),linked.Token).ConfigureAwait(false);}if(expectedSize is >0&&total!=expectedSize.Value)throw new InvalidOperationException($"Размер файла не совпал: expected={expectedSize.Value}, actual={total}");await output.FlushAsync(linked.Token).ConfigureAwait(false);}finally{if(buffer is not null)ArrayPool<byte>.Shared.Return(buffer);}
    }

    private static void RequireInstallerHash(string path,string expected){expected=NeoForgeDistributionBootstrap.NormalizeSha256(expected);if(!NeoForgeDistributionBootstrap.IsSha256(expected))throw new InvalidOperationException("Invalid expected installer SHA-256.");using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);var actual=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();if(!string.Equals(actual,expected,StringComparison.Ordinal))throw new InvalidOperationException($"NeoForge SHA-256 mismatch: expected={expected}, actual={actual}");}

    private static void ValidateInstallerMetadata(string path,string loaderVersion)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);using var zip=new ZipArchive(file,ZipArchiveMode.Read);var matched=false;
        foreach(var entry in zip.Entries.Where(static entry=>(entry.FullName.EndsWith("install_profile.json",StringComparison.OrdinalIgnoreCase)||entry.FullName.EndsWith("version.json",StringComparison.OrdinalIgnoreCase))&&entry.Length>0&&entry.Length<=MaxMetadataEntryBytes)){using var stream=entry.Open();using var reader=new StreamReader(stream,Encoding.UTF8,true);var text=reader.ReadToEnd();if(text.Contains("neoforge",StringComparison.OrdinalIgnoreCase)&&text.Contains(loaderVersion,StringComparison.OrdinalIgnoreCase)){matched=true;break;}}
        if(!matched)throw new InvalidOperationException("NeoForge installer metadata does not match requested version.");
    }

    private async Task<string?> InstallOriginalJarAsync(string installerPath,string minecraftVersion,string loaderVersion,string expectedId,NeoForgeDistributionSpec distribution,CancellationToken ct)
    {
        var root=_path.BasePath??throw new InvalidOperationException("MinecraftPath.BasePath пустой.");var java=await FindJavaAsync(ct).ConfigureAwait(false);Exception? last=null;
        foreach(var mirror in NeoForgeDistributionBootstrap.GetEffectiveMavenMirrors(distribution)){
            ct.ThrowIfCancellationRequested();var normalized=NeoForgeDistributionBootstrap.NormalizeHttpsBase(mirror);if(normalized.Length==0)continue;var source=NeoForgeDistributionBootstrap.DescribeSource(normalized);_log?.Invoke($"NeoForge Maven: {source}");if(source=="BMCLAPI")_log?.Invoke("Источник зависимостей: BMCLAPI");
            var appData=Path.Combine(root,"launcher","tmp","neoforge",Guid.NewGuid().ToString("N"));var tempMc=Path.Combine(appData,".minecraft");
            try{
                Directory.CreateDirectory(tempMc);WriteLauncherProfile(tempMc);SeedVanillaVersion(root,tempMc,minecraftVersion);await PrefetchNeoForgeMavenArtifactsAsync(installerPath,tempMc,normalized,ct).ConfigureAwait(false);
                var env=new Dictionary<string,string>{{"APPDATA",appData},{"LOCALAPPDATA",appData}};
                var result=await RunJavaAsync(java,new[]{"-jar",installerPath,"--installClient",tempMc,distribution.InstallerMirrorArgument,normalized},root,env,ct).ConfigureAwait(false);if(result.ExitCode!=0)throw new InvalidOperationException(result.Error.Length>0?result.Error:result.Output);
                var installed=FindNeoForgeVersion(tempMc,loaderVersion);if(installed is null)throw new InvalidOperationException("Installer did not create NeoForge version JSON.");MergeDir(Path.Combine(tempMc,"versions"),Path.Combine(root,"versions"));MergeDir(Path.Combine(tempMc,"libraries"),Path.Combine(root,"libraries"));MergeDir(Path.Combine(tempMc,"assets"),Path.Combine(root,"assets"));
                if(IsVersionPresent(expectedId))return expectedId;if(IsVersionPresent(installed))return installed;return FindNeoForgeVersion(root,loaderVersion);
            }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch(Exception ex){last=ex;_log?.Invoke($"NeoForge Maven mirror {source} failed: {ex.Message}");}finally{try{if(Directory.Exists(appData))Directory.Delete(appData,true);}catch{}}
        }
        throw new InvalidOperationException("NeoForge installation failed on all Maven mirrors.",last);
    }

    private async Task PrefetchNeoForgeMavenArtifactsAsync(string installerPath,string tempMc,string mirrorBase,CancellationToken ct)
    {
        var artifacts=ReadNeoForgeHostedArtifacts(installerPath);if(artifacts.Count==0){_log?.Invoke("NeoForge Maven prefetch: в installer metadata нет внешних NeoForge artifacts.");return;}
        var librariesRoot=Path.Combine(tempMc,"libraries");Directory.CreateDirectory(librariesRoot);var rootFull=Path.GetFullPath(librariesRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var installedLibrariesRoot=Path.Combine(_path.BasePath??throw new InvalidOperationException("MinecraftPath.BasePath пустой."),"libraries");Directory.CreateDirectory(installedLibrariesRoot);var installedRootFull=Path.GetFullPath(installedLibrariesRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var baseUri=new Uri(mirrorBase,UriKind.Absolute);var downloaded=0;var reused=0;var alreadyReady=0;_log?.Invoke($"NeoForge Maven prefetch: проверяю {artifacts.Count} artifact(s), до {NeoForgePrefetchParallelism} загрузок параллельно.");
        await Parallel.ForEachAsync(artifacts,new ParallelOptions{MaxDegreeOfParallelism=NeoForgePrefetchParallelism,CancellationToken=ct},async(artifact,token)=>{
            var relativePath=NormalizeMavenPath(artifact.Path);if(relativePath.Length==0)throw new InvalidOperationException($"Некорректный Maven path в installer metadata: {artifact.Path}");var relativeLocal=relativePath.Replace('/',Path.DirectorySeparatorChar);var target=Path.GetFullPath(Path.Combine(librariesRoot,relativeLocal));if(!target.StartsWith(rootFull,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Maven artifact path вышел за libraries sandbox.");
            if(File.Exists(target)&&VerifySha1(target,artifact.Sha1,artifact.Size)){Interlocked.Increment(ref alreadyReady);return;}
            TryDelete(target);Directory.CreateDirectory(Path.GetDirectoryName(target)!);var installed=Path.GetFullPath(Path.Combine(installedLibrariesRoot,relativeLocal));if(installed.StartsWith(installedRootFull,StringComparison.OrdinalIgnoreCase)&&File.Exists(installed)&&VerifySha1(installed,artifact.Sha1,artifact.Size)){File.Copy(installed,target,true);Interlocked.Increment(ref reused);return;}
            var temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";TryDelete(temp);try{var sourceUri=new Uri(baseUri,relativePath);await DownloadFileAsync(sourceUri,temp,MaxLibraryArtifactBytes,artifact.Size>0?artifact.Size:null,token).ConfigureAwait(false);if(!VerifySha1(temp,artifact.Sha1,artifact.Size))throw new InvalidOperationException($"SHA-1 mismatch для {relativePath}");File.Move(temp,target,true);Interlocked.Increment(ref downloaded);}finally{TryDelete(temp);}
        }).ConfigureAwait(false);
        _log?.Invoke($"NeoForge Maven prefetch: готово {artifacts.Count}; из кеша {reused}, уже было {alreadyReady}, загружено {downloaded}.");
    }

    private static IReadOnlyList<InstallerArtifact> ReadNeoForgeHostedArtifacts(string installerPath)
    {
        var result=new Dictionary<string,InstallerArtifact>(StringComparer.OrdinalIgnoreCase);using var file=new FileStream(installerPath,FileMode.Open,FileAccess.Read,FileShare.Read);using var zip=new ZipArchive(file,ZipArchiveMode.Read);
        foreach(var entryName in new[]{"install_profile.json","version.json"}){var entry=zip.Entries.FirstOrDefault(entry=>entry.FullName.Equals(entryName,StringComparison.OrdinalIgnoreCase)||entry.FullName.EndsWith('/'+entryName,StringComparison.OrdinalIgnoreCase));if(entry is null||entry.Length<=0||entry.Length>MaxMetadataEntryBytes)continue;using var stream=entry.Open();using var document=JsonDocument.Parse(stream);if(!document.RootElement.TryGetProperty("libraries",out var libraries)||libraries.ValueKind!=JsonValueKind.Array)continue;foreach(var library in libraries.EnumerateArray()){if(!TryReadArtifact(library,out var artifact))continue;if(!Uri.TryCreate(artifact.Url,UriKind.Absolute,out var originalUri)||originalUri.Scheme!=Uri.UriSchemeHttps||!originalUri.Host.Equals("maven.neoforged.net",StringComparison.OrdinalIgnoreCase))continue;var path=NormalizeMavenPath(artifact.Path);if(path.Length==0||!IsSha1(artifact.Sha1))throw new InvalidOperationException("NeoForge installer содержит Maven artifact без безопасного path/SHA-1.");artifact=artifact with{Path=path,Sha1=artifact.Sha1.ToLowerInvariant()};if(result.TryGetValue(path,out var existing)&&(!string.Equals(existing.Sha1,artifact.Sha1,StringComparison.Ordinal)||(existing.Size>0&&artifact.Size>0&&existing.Size!=artifact.Size)))throw new InvalidOperationException($"Installer metadata содержит конфликтующие hashes для {path}.");result[path]=artifact;}}
        return result.Values.OrderBy(static artifact=>artifact.Path,StringComparer.Ordinal).ToArray();
    }

    private static bool TryReadArtifact(JsonElement library,out InstallerArtifact artifact){artifact=new InstallerArtifact("","",0,"");if(library.ValueKind!=JsonValueKind.Object||!library.TryGetProperty("downloads",out var downloads)||downloads.ValueKind!=JsonValueKind.Object||!downloads.TryGetProperty("artifact",out var node)||node.ValueKind!=JsonValueKind.Object)return false;var path=GetJsonString(node,"path");var sha1=GetJsonString(node,"sha1");var url=GetJsonString(node,"url");long size=0;if(node.TryGetProperty("size",out var sizeNode)&&sizeNode.ValueKind==JsonValueKind.Number)sizeNode.TryGetInt64(out size);if(path.Length==0||url.Length==0)return false;artifact=new InstallerArtifact(path,sha1,size,url);return true;}
    private static string GetJsonString(JsonElement node,string propertyName){if(!node.TryGetProperty(propertyName,out var value)||value.ValueKind!=JsonValueKind.String)return "";return value.GetString()?.Trim()??"";}
    private static string NormalizeMavenPath(string? value){var path=(value??"").Trim().Replace('\\','/').TrimStart('/');if(path.Length==0||path.Contains(':'))return "";var segments=path.Split('/',StringSplitOptions.RemoveEmptyEntries);if(segments.Length==0||segments.Any(static segment=>segment is "." or ".."))return "";return string.Join('/',segments);}
    private static bool IsSha1(string? value){var text=(value??"").Trim();if(text.Length!=40)return false;foreach(var ch in text){if(ch is >= '0' and <= '9')continue;if(ch is >= 'a' and <= 'f')continue;if(ch is >= 'A' and <= 'F')continue;return false;}return true;}
    private static bool VerifySha1(string path,string expectedSha1,long expectedSize){if(!File.Exists(path)||!IsSha1(expectedSha1))return false;try{var info=new FileInfo(path);if(expectedSize>0&&info.Length!=expectedSize)return false;using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);var actual=Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();return string.Equals(actual,expectedSha1.Trim().ToLowerInvariant(),StringComparison.Ordinal);}catch{return false;}}
    private static void SeedVanillaVersion(string root,string tempMc,string minecraftVersion){var source=Path.Combine(root,"versions",minecraftVersion);if(!Directory.Exists(source))return;var destination=Path.Combine(tempMc,"versions",minecraftVersion);MergeDir(source,destination);}

    private async Task<string> FindJavaAsync(CancellationToken ct){var candidates=new List<string>();var root=_path.BasePath??"";try{var runtime=Path.Combine(root,"runtime");if(Directory.Exists(runtime))candidates.AddRange(Directory.EnumerateFiles(runtime,"java.exe",SearchOption.AllDirectories));}catch{}var javaHome=Environment.GetEnvironmentVariable("JAVA_HOME");if(!string.IsNullOrWhiteSpace(javaHome))candidates.Add(Path.Combine(javaHome,"bin",OperatingSystem.IsWindows()?"java.exe":"java"));candidates.Add(OperatingSystem.IsWindows()?"java.exe":"java");foreach(var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase)){var probe=await ProbeJavaAsync(candidate,ct).ConfigureAwait(false);if(probe is {Major:>=21,Is64Bit:true}){_log?.Invoke($"Java: найден {probe.Major} x64 ({candidate}).");return candidate;}}throw new InvalidOperationException("Требуется Java 21+ x64.");}
    private sealed record JavaProbe(int Major,bool Is64Bit);
    private static async Task<JavaProbe?> ProbeJavaAsync(string java,CancellationToken ct){try{using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);linked.CancelAfter(JavaProbeTimeout);var psi=new ProcessStartInfo(java){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};psi.ArgumentList.Add("-XshowSettings:properties");psi.ArgumentList.Add("-version");using var process=Process.Start(psi);if(process is null)return null;var stdoutTask=process.StandardOutput.ReadToEndAsync(linked.Token);var stderrTask=process.StandardError.ReadToEndAsync(linked.Token);await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);var text=(await stdoutTask.ConfigureAwait(false))+"\n"+(await stderrTask.ConfigureAwait(false));if(process.ExitCode!=0)return null;var major=ParseJavaMajor(text);var x64=text.Contains("sun.arch.data.model = 64",StringComparison.OrdinalIgnoreCase)||text.Contains("os.arch = amd64",StringComparison.OrdinalIgnoreCase)||text.Contains("os.arch = x86_64",StringComparison.OrdinalIgnoreCase)||text.Contains("os.arch = aarch64",StringComparison.OrdinalIgnoreCase)||text.Contains("64-Bit Server VM",StringComparison.OrdinalIgnoreCase);return major>0?new JavaProbe(major,x64):null;}catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{return null;}}
    private static int ParseJavaMajor(string text){foreach(var line in text.Split('\n')){var value=line.Trim();if(value.StartsWith("java.version =",StringComparison.OrdinalIgnoreCase)){var version=value["java.version =".Length..].Trim();var token=version.Split('.','-','+')[0];if(int.TryParse(token,out var major)){if(major!=1)return major;var parts=version.Split('.');if(parts.Length>1&&int.TryParse(parts[1],out var legacy))return legacy;}}var marker=value.IndexOf("version \"",StringComparison.OrdinalIgnoreCase);if(marker>=0){var start=marker+"version \"".Length;var end=value.IndexOf('"',start);if(end>start){var version=value[start..end];var token=version.Split('.','-','+')[0];if(int.TryParse(token,out var quotedMajor)){if(quotedMajor!=1)return quotedMajor;var parts=version.Split('.');if(parts.Length>1&&int.TryParse(parts[1],out var legacy))return legacy;}}}}return 0;}

    private async Task<(int ExitCode,string Output,string Error)> RunJavaAsync(string java,IEnumerable<string> args,string workingDirectory,IDictionary<string,string> env,CancellationToken ct){using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);linked.CancelAfter(InstallOverallTimeout);var psi=new ProcessStartInfo(java){WorkingDirectory=workingDirectory,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};foreach(var arg in args)psi.ArgumentList.Add(arg);foreach(var pair in env)psi.Environment[pair.Key]=pair.Value;using var process=Process.Start(psi)??throw new InvalidOperationException("Не удалось запустить Java.");var outputTask=process.StandardOutput.ReadToEndAsync(linked.Token);var errorTask=process.StandardError.ReadToEndAsync(linked.Token);try{await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);}catch(OperationCanceledException){try{if(!process.HasExited)process.Kill(true);}catch{}if(ct.IsCancellationRequested)throw;throw new TimeoutException($"NeoForge installer timeout after {InstallOverallTimeout}.");}var stdout=await outputTask.ConfigureAwait(false);var stderr=await errorTask.ConfigureAwait(false);if(stdout.Length>OutputCapChars)stdout=stdout[^OutputCapChars..];if(stderr.Length>OutputCapChars)stderr=stderr[^OutputCapChars..];return(process.ExitCode,stdout.Trim(),stderr.Trim());}
    private bool IsVersionPresent(string id){var root=_path.BasePath??"";return File.Exists(Path.Combine(root,"versions",id,id+".json"));}
    private static string? FindNeoForgeVersion(string root,string loaderVersion){var versions=Path.Combine(root,"versions");if(!Directory.Exists(versions))return null;return Directory.EnumerateDirectories(versions).Select(Path.GetFileName).Where(static value=>!string.IsNullOrWhiteSpace(value)).Select(static value=>value!).FirstOrDefault(value=>value.Contains("neoforge",StringComparison.OrdinalIgnoreCase)&&value.Contains(loaderVersion,StringComparison.OrdinalIgnoreCase));}
    private static void WriteLauncherProfile(string mcDir){Directory.CreateDirectory(mcDir);var json=JsonSerializer.Serialize(new{profiles=new Dictionary<string,object>(),settings=new Dictionary<string,object>(),launcherVersion=new{name="LegendBorn",format=21}});File.WriteAllText(Path.Combine(mcDir,"launcher_profiles.json"),json);File.WriteAllText(Path.Combine(mcDir,"launcher_profiles_microsoft_store.json"),json);}
    private static void MergeDir(string source,string destination){if(!Directory.Exists(source))return;Directory.CreateDirectory(destination);foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)){var target=Path.Combine(destination,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);}}
    private static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch{}}
}
