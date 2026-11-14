using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearth.Core;

// Explicit installer smoke entry point: verify and execute a locally built installer.
if (args.Length == 2 && args[0] == "--installer-update-probe") {
    using var installer = GithubUpdater.StartInstaller(args[1]);
    await installer.WaitForExitAsync(); Environment.ExitCode = installer.ExitCode; return;
}

// A harmless subprocess probe captures the exact Prism launch arguments.
if (args.Contains("--dir")) {
    var dir = args[Array.IndexOf(args, "--dir") + 1]; Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "prism-handoff-probe.json"), JsonSerializer.Serialize(args)); return;
}
var suite = new IntegrationSuite();
try { await suite.Run(); } catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; } finally { await suite.Finish(); }

sealed class IntegrationSuite
{
    readonly List<object> checks = [];
    readonly string root = FindRoot();
    readonly string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    readonly List<string> logs = [];
    Process? host;
    string url = "", data = "";
    int port, httpsPort;
    bool complete;
    public async Task Run()
    {
        data = Path.Combine(root, "artifacts", "verification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(data);
        InternetAccessChecks.Run(Path.Combine(data, "internet-access"), Check);
        var key = Path.Combine(data,"owner.init.key"); await File.WriteAllTextAsync(key, token); port = FreePort(); url = "http://127.0.0.1:" + port;
        using (var init = Start("--init","--data",Path.Combine(data,"host"),"--owner-token-file",key,"--port",port.ToString())) { await init.WaitForExitAsync(); Check(init.ExitCode == 0,"Host initializes with a hashed owner credential"); }
        File.Delete(key); do { httpsPort = FreePort(); } while (httpsPort == port); await StartAndWait();
        string certificatePin;
        using (var certificate = HostCertificate.LoadOrCreate(Path.Combine(data, "host"))) certificatePin = HostCertificate.GetPin(certificate);
        using (var securedOwner = new HearthApi("https://127.0.0.1:" + httpsPort, token, certificatePin: certificatePin))
            Check((await securedOwner.ListPacksAsync()).Count == 0, "Host CLI exposes its authenticated HTTPS listener alongside loopback management");
        using (var wrongIdentity = new HearthApi("https://127.0.0.1:" + httpsPort, token, certificatePin: new string('0', 64)))
            await Throws<HttpRequestException>(() => wrongIdentity.HealthAsync(), "Real host HTTPS rejects a mismatched certificate identity");
        using (var unpinned = new HearthApi("https://127.0.0.1:" + httpsPort, token))
            await Throws<HttpRequestException>(() => unpinned.HealthAsync(), "Real host self-signed HTTPS requires its pinned identity");
        using var owner = new HearthApi(url, token); using var anon = new HearthApi(url);
        Check(!string.IsNullOrWhiteSpace((await anon.HealthAsync()).HostId), "Public health endpoint exposes service identity");
        var a = await owner.CreatePackAsync(new("Coast & Copper","1.21.1","fabric","0.16.10"));
        var b = await owner.CreatePackAsync(new("Mosslight","1.21.1","fabric","0.16.10"));
        Check((await owner.ListPacksAsync()).Count == 2,"One host manages two independent private packs");
        var alice = await anon.JoinAsync(a.Id,new(a.InviteCode,"Alice")); var bob = await anon.JoinAsync(b.Id,new(b.InviteCode,"Bob")); var forwarded = await anon.JoinAsync(a.Id,new(a.InviteCode,"Forwarded invite recipient"));
        using var aa = new HearthApi(url,alice.Token); using var ba = new HearthApi(url,bob.Token); using var fa = new HearthApi(url,forwarded.Token);
        Check((await aa.AccessAsync(a.Id)).Status == "pending","Invite requests begin pending");
        await Denied(()=>aa.ManifestAsync(a.Id),"Pending member cannot read metadata"); await Denied(()=>anon.ManifestAsync(a.Id),"Anonymous metadata download denied");
        await owner.UpdateMemberAsync(a.Id,alice.MemberId,new("approved")); await owner.UpdateMemberAsync(b.Id,bob.MemberId,new("approved"));
        Check((await aa.AccessAsync(a.Id)).Status == "approved","Owner approves membership per pack");
        await Denied(()=>aa.MembersAsync(a.Id),"Player cannot manage membership"); await Denied(()=>aa.UpdateMemberAsync(a.Id,forwarded.MemberId,new("approved","owner")),"Player cannot promote another member");
        var custom1 = await Jar("v1","custom.jar","custom one"); var beta = await Jar("beta","moss.jar","private beta mod");
        await owner.UploadAsync(a.Id,custom1.Path,custom1.File); await owner.UploadAsync(b.Id,beta.Path,beta.File);
        await RawDenied(a.Id,custom1.File.Sha256,forwarded.Token,"Forwarded invite cannot download staged bytes");
        var r1 = await owner.PublishAsync(a.Id,Publish(a,[custom1.File],null)); var rb = await owner.PublishAsync(b.Id,Publish(b,[beta.File],null));
        Check((await aa.ManifestAsync(a.Id)).ReleaseId == r1.ReleaseId,"Approved player receives complete published release");
        await Denied(()=>aa.ManifestAsync(b.Id),"Pack A member cannot read pack B metadata");
        await RawDenied(b.Id,beta.File.Sha256,alice.Token,"Pack A member cannot download pack B bytes"); await RawDenied(a.Id,custom1.File.Sha256,bob.Token,"Pack B member cannot download pack A bytes"); await RawDenied(a.Id,custom1.File.Sha256,null,"Anonymous file download denied");
        await Denied(()=>fa.ManifestAsync(a.Id),"Forwarded invite still requires separate approval"); await Denied(()=>aa.PublishAsync(a.Id,Publish(a,[],r1.ReleaseId)),"Player cannot publish a release");
        await Rejected(()=>owner.PublishAsync(a.Id,Publish(a,[new("mods/missing.jar",new string('a',64),100)],r1.ReleaseId)),"Incomplete release cannot be published");
        Check((await aa.ManifestAsync(a.Id)).ReleaseId == r1.ReleaseId,"Failed publication preserves prior complete release");
        await Rejected(()=>owner.PublishAsync(a.Id,Publish(a,[custom1.File with {Path="mods/../options.txt"}],r1.ReleaseId)),"Host rejects traversal paths");
        await Rejected(()=>owner.PublishAsync(a.Id,Publish(a,[custom1.File],"stale-release")),"Stale draft cannot overwrite a newer release");

        var ap = new JoinedPack(url,a.Id,a.Name,alice.Token,alice.MemberId); var bp = new JoinedPack(url,b.Id,b.Name,bob.Token,bob.MemberId);
        var sync = new PackSynchronizer(Path.Combine(data,"profiles","alice")); var bs = new PackSynchronizer(Path.Combine(data,"profiles","bob"));
        var first = await sync.SyncAsync(aa,ap); var bobFirst = await bs.SyncAsync(ba,bp);
        Check(first.DownloadedFiles == 1 && bobFirst.DownloadedFiles == 1 && first.InstanceDirectory != bobFirst.InstanceDirectory,"Separate profiles install their approved packs");
        var mc = Path.Combine(first.InstanceDirectory,".minecraft");
        var personal = new Dictionary<string,byte[]> { ["options.txt"]=Encoding.UTF8.GetBytes("key_key.forward:87\ngraphicsMode:2\n"), ["servers.dat"]=Encoding.UTF8.GetBytes("personal server list"), ["saves/My local world/level.dat"]=RandomNumberGenerator.GetBytes(2048), ["config/personal.json"]=Encoding.UTF8.GetBytes("{\"local\":true}"), ["screenshots/build.png"]=RandomNumberGenerator.GetBytes(256), ["mods/personal.jar"]=Encoding.UTF8.GetBytes("unmanaged personal mod") };
        foreach(var (name,bytes) in personal) { var path=Path.Combine(mc,name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path,bytes); }
        var cfg=Path.Combine(first.InstanceDirectory,"instance.cfg"); await File.AppendAllTextAsync(cfg,"\r\nOverrideMemory=true\r\nMaxMemAlloc=6144\r\n"); var cfgText=await File.ReadAllTextAsync(cfg);
        Check((await sync.SyncAsync(aa,ap)).DownloadedFiles == 0,"Unchanged Play downloads no JARs");
        var custom2=await Jar("v2","custom.jar","custom two",2*1024*1024); await owner.UploadAsync(a.Id,custom2.Path,custom2.File);
        Check((await aa.ManifestAsync(a.Id)).ReleaseId == r1.ReleaseId,"Uploading replacement does not expose unfinished draft");
        var r2=await owner.PublishAsync(a.Id,Publish(a,[custom2.File],r1.ReleaseId));
        using(var faultHttp=new HttpClient(new InterruptHandler(custom2.File.Sha256))) using(var faultApi=new HearthApi(url,alice.Token,faultHttp)) await Throws<IOException>(()=>sync.SyncAsync(faultApi,ap),"Interrupted transfer reports a retryable failure");
        Check(await ModFiles.MatchesAsync(Path.Combine(mc,"mods/custom.jar"),custom1.File),"Interrupted transfer leaves old JAR intact");
        Check(sync.GetInstalled(url,a.Id)!.ReleaseId == r1.ReleaseId,"Interrupted transfer leaves complete installation selected");
        Check(!Directory.EnumerateFiles(Path.Combine(data,"profiles","alice"),"*.part",SearchOption.AllDirectories).Any(),"Failed partial download cleaned up");
        var retry=await sync.SyncAsync(aa,ap); Check(retry.DownloadedFiles == 1 && await ModFiles.MatchesAsync(Path.Combine(mc,"mods/custom.jar"),custom2.File),"Retry installs verified custom replacement");
        await Recovery(sync,ap,mc,custom2.File);

        var dep=await Jar("terrain","shared-api.jar","base dependency"); var td=await Jar("terrain","terrain-api.jar","terrain dependency"); var voxy=await Jar("terrain","voxy.jar","voxy fixture",group:"voxy"); var dh=await Jar("terrain","distant-horizons.jar","dh fixture",group:"distanthorizons");
        foreach(var j in new[]{dep,td,voxy,dh}) await owner.UploadAsync(a.Id,j.Path,j.File);
        List<PackFile> terrain=[custom2.File,dep.File,dep.File with {Group="voxy"},dep.File with {Group="distanthorizons"},td.File with {Group="voxy"},td.File with {Group="distanthorizons"},voxy.File,dh.File];
        var r3=await owner.PublishAsync(a.Id,Publish(a,terrain,r2.ReleaseId)); await sync.SyncAsync(aa,ap with {Terrain=TerrainMode.Voxy});
        Check(File.Exists(Path.Combine(mc,"mods/voxy.jar")) && File.Exists(Path.Combine(mc,"mods/terrain-api.jar")) && !File.Exists(Path.Combine(mc,"mods/distant-horizons.jar")),"Voxy installs with required dependencies");
        var ds=await sync.SyncAsync(aa,ap with {Terrain=TerrainMode.DistantHorizons});
        Check(ds.DownloadedFiles==1 && File.Exists(Path.Combine(mc,"mods/distant-horizons.jar")) && !File.Exists(Path.Combine(mc,"mods/voxy.jar")) && File.Exists(Path.Combine(mc,"mods/terrain-api.jar")),"Distant Horizons switch retains shared dependencies");
        await sync.SyncAsync(aa,ap);
        Check(!File.Exists(Path.Combine(mc,"mods/terrain-api.jar")) && !File.Exists(Path.Combine(mc,"mods/distant-horizons.jar")) && File.Exists(Path.Combine(mc,"mods/shared-api.jar")),"Off removes optional mods and preserves base dependency");
        Check(sync.GetInstalled(url,a.Id)!.Terrain == TerrainMode.Off,"Installed terrain choice recorded per pack");
        var r4=await owner.PublishAsync(a.Id,Publish(a,[dep.File],r3.ReleaseId)); var removed=await sync.SyncAsync(aa,ap);
        Check(removed.RemovedFiles==1 && !File.Exists(Path.Combine(mc,"mods/custom.jar")),"Removing custom JAR removes its managed file");
        foreach(var (name,bytes) in personal) Check((await File.ReadAllBytesAsync(Path.Combine(mc,name))).SequenceEqual(bytes),"Personal file preserved: "+name);
        Check(await File.ReadAllTextAsync(cfg)==cfgText,"Personal Prism settings survive updates");
        var conflict=await Jar("conflict","personal.jar","owner filename conflict"); await owner.UploadAsync(a.Id,conflict.Path,conflict.File);
        var r5=await owner.PublishAsync(a.Id,Publish(a,[dep.File,conflict.File],r4.ReleaseId)); await Throws<IOException>(()=>sync.SyncAsync(aa,ap),"Update refuses to overwrite unknown personal mod");
        Check(sync.GetInstalled(url,a.Id)!.ReleaseId==r4.ReleaseId,"Personal-file conflict preserves installed release");
        var duplicate1=await Jar("duplicates","duplicate-one.jar","first copy",modId:"hearth_duplicate"); var duplicate2=await Jar("duplicates","duplicate-two.jar","second copy",modId:"hearth_duplicate");
        await owner.UploadAsync(a.Id,duplicate1.Path,duplicate1.File); await owner.UploadAsync(a.Id,duplicate2.Path,duplicate2.File);
        await Rejected(()=>owner.PublishAsync(a.Id,Publish(a,[dep.File,duplicate1.File,duplicate2.File],r5.ReleaseId)),"Host rejects duplicate Fabric mod IDs before publication");
        var malformed=r5 with { Files=[dep.File,duplicate1.File,duplicate2.File] };
        using(var badHostHttp=new HttpClient(new ManifestFixtureHandler(malformed,new() { [duplicate1.File.Sha256]=await File.ReadAllBytesAsync(duplicate1.Path), [duplicate2.File.Sha256]=await File.ReadAllBytesAsync(duplicate2.Path) })))
        using(var badHost=new HearthApi(url,alice.Token,badHostHttp))
            await Throws<InvalidDataException>(()=>sync.SyncAsync(badHost,ap),"Client independently rejects duplicate Fabric mod IDs before installation");
        Check(sync.GetInstalled(url,a.Id)!.ReleaseId==r4.ReleaseId && !File.Exists(Path.Combine(mc,"mods/duplicate-one.jar")),"Duplicate-mod rejection preserves prior complete installation");
        await owner.UpdateMemberAsync(a.Id,alice.MemberId,new("removed")); await Denied(()=>aa.ManifestAsync(a.Id),"Removed member loses metadata access"); await RawDenied(a.Id,dep.File.Sha256,alice.Token,"Removed member loses file access");
        Check((await ba.ManifestAsync(b.Id)).ReleaseId==rb.ReleaseId,"Revocation in A does not affect B");
        var returning=await anon.JoinAsync(a.Id,new(a.InviteCode,"Alice rejoining")); using var promoted=new HearthApi(url,returning.Token);
        await owner.UpdateMemberAsync(a.Id,returning.MemberId,new("approved","owner")); Check((await promoted.MembersAsync(a.Id)).Count==2,"Pack owner can manage their members"); await Denied(()=>promoted.MembersAsync(b.Id),"Owner role cannot cross pack boundaries");
        await Denied(()=>aa.ManifestAsync(a.Id),"Removed credential stays revoked after a fresh join");
        Check((await promoted.PublishAsync(a.Id,Publish(a,[dep.File],r5.ReleaseId))).ReleaseId != r5.ReleaseId,"Authorized pack owner can publish");
        Stop(); await StartAndWait(); Check((await ba.ManifestAsync(b.Id)).ReleaseId==rb.ReleaseId && (await promoted.AccessAsync(a.Id)).Role=="owner","Host restart preserves releases and access");
        using (var restartedOwner = new HearthApi("https://127.0.0.1:" + httpsPort, token, certificatePin: certificatePin))
            Check((await restartedOwner.ListPacksAsync()).Count == 2, "Actual host restart preserves the trusted HTTPS certificate and owner access");
        var persisted=string.Join('\n',Directory.EnumerateFiles(Path.Combine(data,"host"),"*.json",SearchOption.AllDirectories).Select(File.ReadAllText)); Check(!persisted.Contains(token)&&!persisted.Contains(alice.Token)&&!persisted.Contains(bob.Token),"No plaintext bearer credentials in host JSON");
        using(var metadata=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(first.InstanceDirectory,"mmc-pack.json")))) Check(metadata.RootElement.GetProperty("components").EnumerateArray().Any(x=>x.GetProperty("uid").GetString()=="net.fabricmc.fabric-loader"),"Prism metadata includes selected loader");
        await Integrations(first);
        FlowInputChecks.Run(Check);
        await SecureHostChecks.RunAsync(Path.Combine(data, "secure-host-checks"), Check);
        await InstalledPlayChecks.RunAsync(Path.Combine(data, "offline-play-checks"), Check);
        complete=true; Console.WriteLine($"PASS: {checks.Count} checks. Evidence: {Path.Combine(data,"report.json")}");
    }

    async Task Recovery(PackSynchronizer sync,JoinedPack pack,string mc,PackFile expected) {
        var txn=Path.Combine(sync.GetInstanceDirectory(pack.HostUrl,pack.PackId),".hearth","transaction"); Directory.CreateDirectory(Path.Combine(txn,"backup")); File.Copy(Path.Combine(mc,"mods/custom.jar"),Path.Combine(txn,"backup","0"));
        await File.WriteAllTextAsync(Path.Combine(txn,"journal.json"),"{\"phase\":\"prepared\",\"entries\":[{\"path\":\".minecraft/mods/custom.jar\",\"existed\":true,\"backup\":\"0\"}]}"); await File.WriteAllTextAsync(Path.Combine(mc,"mods/custom.jar"),"simulated exit during commit"); await sync.RecoverAsync(pack.HostUrl,pack.PackId);
        Check(await ModFiles.MatchesAsync(Path.Combine(mc,"mods/custom.jar"),expected),"Recovery restores a partial commit from durable backup");
    }
    async Task Integrations(SyncResult installed) {
        var profile=Path.Combine(data,"profiles","alice"); var prism=new PrismService(profile); var probe=Path.Combine(AppContext.BaseDirectory,"Hearth.Tests.exe");
        using(var p=prism.Launch(probe,installed.InstanceId)) { await p.WaitForExitAsync(); Check(p.ExitCode==0,"Prism launch handoff starts child process"); }
        var captured=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(profile,"Prism","prism-handoff-probe.json")))!;
        Check(captured.SequenceEqual(new[]{"--dir",Path.Combine(profile,"Prism"),"--launch",installed.InstanceId}),"Prism handoff passes isolated data root and persistent instance ID");
        var handler=new GithubFixtureHandler(); using var http=new HttpClient(handler); using var updater=new GithubUpdater(http,new Uri("http://localhost/"));
        var release=await updater.CheckAsync("example/hearth",new Version(1,0,0)); Check(release?.Version=="1.0.1","Updater check detects newer GitHub release");
        Check(handler.AssetRequests==0,"Check does not download an update");
        Check(await updater.CheckAsync("example/hearth",new Version(2,0,0))==null,"Updater rejects downgrades");
        Check(await updater.CheckAsync("example/hearth",new Version(1,0,1,0))==null,"Updater treats a three-part tag and four-part assembly version as equal");
        var download=await updater.DownloadAsync(release!,Path.Combine(data,"updates")); Check(await ModFiles.HashAsync(download)==release!.Sha256,"Updater downloads and verifies installer digest");
        var requests=handler.AssetRequests;
        Check(await updater.DownloadAsync(release!,Path.Combine(data,"updates"))==download && handler.AssetRequests==requests,"Verified cached installer is reused without another download");
        handler.Corrupt=true; await Throws<IOException>(()=>updater.DownloadAsync(release!,Path.Combine(data,"bad-update")),"Updater discards corrupt installer");
        Check(!Directory.EnumerateFiles(Path.Combine(data,"bad-update")).Any(),"Corrupt update leaves no executable or partial file");
        await Throws<IOException>(()=>updater.DownloadAsync(release! with {Sha256=new string('f',64)},Path.Combine(data,"updates")),"Invalid replacement download is rejected");
        Check(await ModFiles.HashAsync(download)==release!.Sha256,"Failed replacement preserves the previous verified cached installer");
        using(var mismatchHttp=new HttpClient(new GithubFixtureHandler{Tag="v1.0.2"}))
        using(var mismatch=new GithubUpdater(mismatchHttp,new Uri("http://localhost/")))
            await Throws<InvalidDataException>(()=>mismatch.CheckAsync("example/hearth",new Version(1,0,0)),"Installer filename must match its release tag");
        var checksumHandler=new GithubFixtureHandler{IncludeDigest=false,IncludeChecksum=true};
        using(var checksumHttp=new HttpClient(checksumHandler))
        using(var checksums=new GithubUpdater(checksumHttp,new Uri("http://localhost/"))) {
            var fallback=await checksums.CheckAsync("example/hearth",new Version(1,0,0));
            Check(fallback!.Sha256==release!.Sha256 && checksumHandler.ChecksumRequests==1,"SHA256SUMS fallback identifies the exact installer");
        }
        using(var externalHttp=new HttpClient(new GithubFixtureHandler{DownloadUrl="https://example.invalid/installer.exe"}))
        using(var external=new GithubUpdater(externalHttp,new Uri("http://localhost/")))
            await Throws<InvalidDataException>(()=>external.CheckAsync("example/hearth",new Version(1,0,0)),"Updater rejects an asset outside its configured GitHub repository");
        using(var missingHttp=new HttpClient(new GithubFixtureHandler{MetadataStatus=HttpStatusCode.NotFound}))
        using(var missing=new GithubUpdater(missingHttp,new Uri("http://localhost/")))
            await Throws<ReleaseUnavailableException>(()=>missing.CheckAsync("example/hearth",new Version(1,0,0)),"A repository without a stable release reports an expected unavailable state");
        Check(GithubUpdater.NormalizeRepository("  example/hearth  ")=="example/hearth","Advanced update repository input is normalized");
        await Throws<ArgumentException>(()=>Task.Run(()=>GithubUpdater.NormalizeRepository("../hearth")),"Invalid update repository paths are rejected");
        await File.AppendAllTextAsync(download,"tampered"); await Throws<IOException>(()=>Task.Run(()=>GithubUpdater.StartInstaller(download)),"Installer handoff rechecks digest before execution");
    }
    async Task<(string Path,PackFile File)> Jar(string folder,string name,string content,int random=0,string group="base",string? modId=null) {
        var path=Path.Combine(data,"jars",folder,name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using(var zip=ZipFile.Open(path,ZipArchiveMode.Create)) { using(var e=new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open())) e.Write("Manifest-Version: 1.0\n\n"); using(var e=new StreamWriter(zip.CreateEntry("fixture.txt").Open())) e.Write(content); if(modId!=null) { using var e=new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()); e.Write(JsonSerializer.Serialize(new{schemaVersion=1,id=modId,version="1.0.0"})); } if(random>0) { using var e=zip.CreateEntry("random.bin",CompressionLevel.NoCompression).Open(); e.Write(RandomNumberGenerator.GetBytes(random)); } } return(path,await ModFiles.InspectAsync(path,group));
    }
    static PublishRequest Publish(PackSummary p,List<PackFile> files,string? expected)=>new(p.Name,p.MinecraftVersion,p.Loader,p.LoaderVersion,files,"Integration fixture",expected);
    async Task RawDenied(string pack,string sha,string? bearer,string label) { using var c=new HttpClient(); using var q=new HttpRequestMessage(HttpMethod.Get,url+$"/packs/{pack}/blobs/{sha}"); if(bearer!=null) q.Headers.Authorization=new("Bearer",bearer); using var r=await c.SendAsync(q); Check(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,label); }
    async Task Denied(Func<Task> action,string label) { try { await action(); throw new Exception("Unauthorized success: "+label); } catch(HostApiException ex) when(ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { Check(true,label); } }
    async Task Rejected(Func<Task> action,string label) { try { await action(); throw new Exception("Invalid request accepted: "+label); } catch(HostApiException ex) when((int)ex.StatusCode is >=400 and <500) { Check(true,label); } }
    async Task Throws<T>(Func<Task> action,string label) where T:Exception { try { await action(); throw new Exception("Expected failure missing: "+label); } catch(T) { Check(true,label); } }
    void Check(bool ok,string label) { checks.Add(new{name=label,passed=ok}); Console.WriteLine((ok?"PASS ":"FAIL ")+label); if(!ok) throw new Exception(label); }
    async Task StartAndWait() { host=Start("--data",Path.Combine(data,"host"),"--port",port.ToString(),"--bind","127.0.0.1","--https-port",httpsPort.ToString()); using var api=new HearthApi(url); for(var i=0;i<100;i++) { if(host.HasExited) throw new Exception("Host exited: "+string.Join('\n',logs)); try { await api.HealthAsync(); return; } catch(HttpRequestException) { await Task.Delay(150); } } throw new TimeoutException("Host failed to start."); }
    Process Start(params string[] args) { var dll=Path.Combine(root,"src","Hearth.Host","bin","Release","net10.0","Hearth.Host.dll"); if(!File.Exists(dll)) dll=dll.Replace("Release","Debug"); var dotnet=Path.Combine(root,"tools","dotnet","dotnet.exe"); if(!File.Exists(dotnet))dotnet="dotnet"; var si=new ProcessStartInfo(dotnet){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}; si.ArgumentList.Add(dll); foreach(var a in args) si.ArgumentList.Add(a); var p=Process.Start(si)!; p.OutputDataReceived+=(_,e)=>{if(e.Data!=null)lock(logs)logs.Add(e.Data);}; p.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)lock(logs)logs.Add(e.Data);}; p.BeginOutputReadLine(); p.BeginErrorReadLine(); return p; }
    void Stop() { if(host is {HasExited:false}) { host.Kill(true); host.WaitForExit(); } host?.Dispose(); host=null; }
    public async Task Finish() { Stop(); if(data.Length>0) { await File.WriteAllTextAsync(Path.Combine(data,"report.json"),JsonSerializer.Serialize(new{complete,generatedUtc=DateTimeOffset.UtcNow,hostUrl=url,profileRoot=Path.Combine(data,"profiles"),checks},JsonDefaults.Options)); await File.WriteAllLinesAsync(Path.Combine(data,"host.log"),logs); await File.WriteAllTextAsync(Path.Combine(root,"artifacts","verification","latest.txt"),data); } }
    static int FreePort() { var s=new TcpListener(IPAddress.Loopback,0); s.Start(); var port=((IPEndPoint)s.LocalEndpoint).Port; s.Stop(); return port; }
    static string FindRoot() { var d=new DirectoryInfo(AppContext.BaseDirectory); while(d!=null&&!File.Exists(Path.Combine(d.FullName,"Hearth.sln")))d=d.Parent; return d?.FullName??throw new Exception("Run within Hearth checkout."); }
}

sealed class InterruptHandler(string hash):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false}) {
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage q,CancellationToken ct) { var r=await base.SendAsync(q,ct); if(q.RequestUri!.AbsolutePath.EndsWith("/blobs/"+hash,StringComparison.Ordinal)) { var original=r.Content; var stream=await original.ReadAsStreamAsync(ct); r.Content=new StreamContent(new InterruptedStream(stream)); r.Content.Headers.ContentLength=original.Headers.ContentLength; } return r; }
}
sealed class InterruptedStream(Stream inner):Stream {
    long bytes;
    public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false; public override long Length=>throw new NotSupportedException(); public override long Position{get=>bytes;set=>throw new NotSupportedException();}
    public override int Read(byte[] b,int o,int c) { if(bytes>=1024)throw new IOException("Injected partial transfer reset"); var n=inner.Read(b,o,Math.Min(c,1024)); bytes+=n;return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default) { if(bytes>=1024)throw new IOException("Injected partial transfer reset");var n=await inner.ReadAsync(b[..Math.Min(b.Length,1024)],ct);bytes+=n;return n; }
    public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException(); protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
}
sealed class GithubFixtureHandler:HttpMessageHandler {
    public int AssetRequests; public int ChecksumRequests; public bool Corrupt;
    public string Tag="v1.0.1"; public string AssetName="Hearth-Setup-1.0.1-win-x64.exe";
    public string DownloadUrl="http://localhost/installer"; public bool IncludeDigest=true; public bool IncludeChecksum;
    public HttpStatusCode MetadataStatus=HttpStatusCode.OK;
    readonly byte[] payload=Encoding.UTF8.GetBytes("Harmless Hearth update fixture. Never executed.");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage q,CancellationToken ct) {
        HttpContent content;
        var hash=Convert.ToHexStringLower(SHA256.HashData(payload));
        if(q.RequestUri!.AbsolutePath.EndsWith("/releases/latest")) {
            if(MetadataStatus!=HttpStatusCode.OK)return Task.FromResult(new HttpResponseMessage(MetadataStatus));
            List<object> assets=[new{name=AssetName,size=payload.Length,digest=IncludeDigest?"sha256:"+hash:null,browser_download_url=DownloadUrl}];
            if(IncludeChecksum)assets.Add(new{name="SHA256SUMS.txt",browser_download_url="http://localhost/checksums"});
            var json=new{tag_name=Tag,draft=false,prerelease=false,body="Integration fixture",html_url="http://localhost/release",assets};content=new StringContent(JsonSerializer.Serialize(json),Encoding.UTF8,"application/json");
        }
        else if(q.RequestUri.AbsolutePath.EndsWith("/checksums")) { ChecksumRequests++;content=new StringContent(hash+"  "+AssetName+"\n"); }
        else { AssetRequests++; var bytes=payload.ToArray();if(Corrupt)bytes[0]^=1;content=new ByteArrayContent(bytes); }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});
    }
}
sealed class ManifestFixtureHandler(PackManifest manifest,Dictionary<string,byte[]> blobs):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false}) {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage q,CancellationToken ct) {
        if(q.RequestUri!.AbsolutePath.EndsWith("/manifest")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(manifest,JsonDefaults.Options),Encoding.UTF8,"application/json")});
        var hash=q.RequestUri.Segments.Last(); if(blobs.TryGetValue(hash,out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
        return base.SendAsync(q,ct);
    }
}
