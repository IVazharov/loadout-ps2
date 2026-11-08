using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using Loadout.Fakes;

namespace Loadout.Updates;

public sealed class UpdateInstallerTests : IDisposable
{
    private static readonly AppVersion Next = AppVersion.Parse("0.10.1");

    private readonly string _temp = Directory.CreateTempSubdirectory("loadout-update-").FullName;

    private string AppFolder => Path.Combine(_temp, "app");

    private string WorkFolder => Path.Combine(AppFolder, "data", "update");

    public void Dispose() => TempDirectory.Delete(_temp);

    [Fact]
    public void AReleaseIsUnpackedAndChecked()
    {
        var check = new FakeCheck(null);
        var installer = Installer(check);
        var zip = Zip(("LoadoutPS2.exe", "new exe"), ("README.md", "new readme"), ("third_party/a/LICENSE", "a"));

        var update = installer.Prepare(zip, Next);

        Assert.Equal(Next, update.Version);
        Assert.Equal(["LoadoutPS2.exe", "README.md", Path.Combine("third_party", "a", "LICENSE")], update.Files.Order(StringComparer.Ordinal));
        Assert.Equal(Path.Combine(update.Folder, "LoadoutPS2.exe"), check.Checked);
        Assert.Equal(Next, check.Version);
    }

    [Fact]
    public void ADownloadThatFailsItsCheckChangesNothing()
    {
        var installer = Installer(new FakeCheck("The download is not signed."));
        var zip = Zip(("LoadoutPS2.exe", "new exe"));

        var error = Assert.Throws<UpdateInstallException>(() => installer.Prepare(zip, Next));

        Assert.Equal("The download is not signed. Nothing was changed.", error.Message);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(AppFolder, "LoadoutPS2.exe")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("data/../../evil.txt")]
    [InlineData("LoadoutPS2.exe.old")]
    public void AFileThatWouldLandOutsideTheFolderIsRefused(string name)
    {
        var installer = Installer(new FakeCheck(null));
        var zip = Zip(("LoadoutPS2.exe", "new exe"), (name, "x"));

        Assert.Throws<UpdateInstallException>(() => installer.Prepare(zip, Next));
        Assert.False(File.Exists(Path.Combine(_temp, "evil.txt")));
    }

    [Fact]
    public void AZipWithoutTheExecutableIsRefused()
    {
        var installer = Installer(new FakeCheck(null));

        var error = Assert.Throws<UpdateInstallException>(() => installer.Prepare(Zip(("README.md", "x")), Next));

        Assert.Contains("LoadoutPS2.exe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplySwapsTheFilesKeepsTheOldOnesAndCleanUpRemovesThem()
    {
        var installer = Installer(new FakeCheck(null));
        var update = installer.Prepare(Zip(("LoadoutPS2.exe", "new exe"), ("README.md", "new readme"), ("third_party/a/LICENSE", "a")), Next);

        installer.Apply(update);

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(AppFolder, "LoadoutPS2.exe")));
        Assert.Equal("new readme", File.ReadAllText(Path.Combine(AppFolder, "README.md")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(AppFolder, "third_party", "a", "LICENSE")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(AppFolder, "LoadoutPS2.exe.old")));
        Assert.Equal("old readme", File.ReadAllText(Path.Combine(AppFolder, "README.md.old")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(AppFolder, "data", "loadout.sqlite")));

        Assert.True(installer.CleanUp());

        Assert.False(File.Exists(Path.Combine(AppFolder, "LoadoutPS2.exe.old")));
        Assert.False(File.Exists(Path.Combine(AppFolder, "README.md.old")));
        Assert.False(Directory.Exists(update.Folder));
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(AppFolder, "LoadoutPS2.exe")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(AppFolder, "data", "loadout.sqlite")));
    }

    [Fact]
    public void ASwapThatFailsPartWayPutsBackWhatItChanged()
    {
        var installer = Installer(new FakeCheck(null));
        File.WriteAllText(Path.Combine(AppFolder, "b.txt"), "old b");
        var update = installer.Prepare(Zip(("LoadoutPS2.exe", "new exe"), ("a.txt", "new a"), ("b.txt", "new b")), Next);

        // b.txt is open in another program, so it can't be moved aside.
        using (new FileStream(Path.Combine(AppFolder, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<UpdateInstallException>(() => installer.Apply(update));
            Assert.Contains("unchanged", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(AppFolder, "LoadoutPS2.exe")));
        Assert.False(File.Exists(Path.Combine(AppFolder, "a.txt")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(AppFolder, "b.txt")));
        Assert.Empty(Directory.GetFiles(AppFolder, "*.old"));
    }

    [Fact]
    public void AFolderLoadoutCanWriteToIsSeenAsSo()
    {
        Assert.True(Installer(new FakeCheck(null)).CanWriteAppFolder());
        Assert.Empty(Directory.GetFiles(AppFolder, "*.tmp"));
    }

    [Fact]
    public async Task TheDownloadIsSavedWithItsProgress()
    {
        var body = new byte[200_000];
        Random.Shared.NextBytes(body);
        var installer = Installer(new FakeCheck(null), new Handler(HttpStatusCode.OK, body));
        var seen = new List<long>();

        var zip = await installer.DownloadAsync(Offer(body.Length), new SyncProgress(seen.Add), CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(zip));
        Assert.Equal(body.Length, seen[^1]);
        Assert.False(File.Exists(zip + ".part"));
    }

    [Theory]
    [InlineData(150_000, "ended early")]
    [InlineData(250_000, "larger")]
    public async Task ADownloadOfTheWrongSizeIsRefused(int size, string said)
    {
        var installer = Installer(new FakeCheck(null), new Handler(HttpStatusCode.OK, new byte[size]));

        var error = await Assert.ThrowsAsync<UpdateInstallException>(() => installer.DownloadAsync(Offer(200_000), null, CancellationToken.None));

        Assert.Contains(said, error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(WorkFolder, "download")));
    }

    [Fact]
    public async Task AFailedDownloadSaysSo()
    {
        var installer = Installer(new FakeCheck(null), new Handler(HttpStatusCode.NotFound, []));

        var error = await Assert.ThrowsAsync<UpdateInstallException>(() => installer.DownloadAsync(Offer(10), null, CancellationToken.None));

        Assert.Contains("404", error.Message, StringComparison.Ordinal);
    }

    private UpdateInstaller Installer(IExecutableCheck check, HttpMessageHandler? handler = null)
    {
        Directory.CreateDirectory(Path.Combine(AppFolder, "data"));
        if (!File.Exists(Path.Combine(AppFolder, "LoadoutPS2.exe")))
        {
            File.WriteAllText(Path.Combine(AppFolder, "LoadoutPS2.exe"), "old exe");
            File.WriteAllText(Path.Combine(AppFolder, "README.md"), "old readme");
            File.WriteAllText(Path.Combine(AppFolder, "data", "loadout.sqlite"), "keep");
        }

        return new UpdateInstaller(new HttpClient(handler ?? new Handler(HttpStatusCode.OK, [])), "0.10.0", AppFolder, WorkFolder, check);
    }

    private string Zip(params (string Name, string Text)[] files)
    {
        var path = Path.Combine(_temp, "release-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }

        return path;
    }

    private static UpdateOffer Offer(long size)
    {
        var download = new ReleaseAsset("Loadout-PS2-0.10.1-win-x64.zip", new Uri("https://example.invalid/Loadout-PS2-0.10.1-win-x64.zip"), size);
        var release = new PublishedRelease(Next, new Uri("https://example.invalid/v0.10.1"), "notes", [download]);
        return new UpdateOffer(release, download, [release]);
    }

    private sealed class FakeCheck(string? problem) : IExecutableCheck
    {
        public string? Checked { get; private set; }

        public AppVersion? Version { get; private set; }

        public string? Problem(string executable, AppVersion version)
        {
            Checked = executable;
            Version = version;
            return problem;
        }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private sealed class Handler(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}

[SupportedOSPlatform("windows")]
public sealed class SignedExecutableCheckTests : IDisposable
{
    // .NET's own library, signed by Microsoft: a real signature to check against.
    private static readonly string Signed = typeof(object).Assembly.Location;

    private readonly string _temp = Directory.CreateTempSubdirectory("loadout-update-").FullName;

    public void Dispose() => TempDirectory.Delete(_temp);

    // Who signed .NET's library: ".NET" now, "Microsoft Corporation" in older runtimes.
    private static string SignerOf(string file)
    {
#pragma warning disable SYSLIB0057
        using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(
            System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
        return certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);
    }

    [Fact]
    public void AFileSignedBySomeoneElseIsRefused()
    {
        var problem = new SignedExecutableCheck().Problem(Signed, AppVersion.Parse("0.10.1"));

        Assert.Equal("The download is signed by " + SignerOf(Signed) + ", not by Open Source Developer Ivan Vazharov.", problem);
    }

    [Fact]
    public void TheRightSignerAndVersionPass()
    {
        var version = AppVersion.Parse(FileVersionInfo.GetVersionInfo(Signed).ProductVersion!);

        var check = new SignedExecutableCheck(SignerOf(Signed));

        Assert.Null(check.Problem(Signed, version));
        Assert.StartsWith("The download is version ", check.Problem(Signed, AppVersion.Parse("0.10.1")), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsignedFileIsRefused()
    {
        var file = Path.Combine(_temp, "LoadoutPS2.exe");
        File.WriteAllBytes(file, new byte[4096]);

        Assert.Equal("The download is not signed.", new SignedExecutableCheck().Problem(file, AppVersion.Parse("0.10.1")));
    }

    [Fact]
    public void AFileChangedAfterSigningIsRefused()
    {
        var file = Path.Combine(_temp, "changed.dll");
        var bytes = File.ReadAllBytes(Signed);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        Assert.Equal("The download was changed after it was signed.", new SignedExecutableCheck().Problem(file, AppVersion.Parse("0.10.1")));
    }
}
