using System.Text.Json;
using RemoteDock.Core;

namespace RemoteDock.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "opencode", "RemoteDock.SessionTests", Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "sessions.json");

    [Fact]
    public void FirstLaunchHasNoPreviousSessions()
    {
        Assert.Empty(new SessionStore(StorePath).Load());
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void OpenTabsAreRecoverableWithoutAShutdownSave()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var store = new SessionStore(StorePath);
        store.Save([first, second, first]);
        Assert.Equal(new[] { first, second, first }, new SessionStore(StorePath).Load());

        store.Save([second, first]);
        Assert.Equal(new[] { second, first }, new SessionStore(StorePath).Load());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ClosingAllTabsOrDecliningRestoreClearsPreviousSessions()
    {
        var store = new SessionStore(StorePath);
        store.Save([Guid.NewGuid()]);
        store.Save([]);
        Assert.Empty(new SessionStore(StorePath).Load());
    }

    [Fact]
    public void FailedSaveKeepsThePreviousSnapshot()
    {
        var store = new SessionStore(StorePath);
        Guid original = Guid.NewGuid();
        store.Save([original]);
        IEnumerable<Guid> FailingIds()
        {
            yield return Guid.NewGuid();
            throw new IOException("Cannot read open tabs.");
        }
        Assert.Throws<IOException>(() => store.Save(FailingIds()));
        Assert.Equal(new[] { original }, new SessionStore(StorePath).Load());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[\"not-a-guid\"]")]
    public void CorruptStateIsNotSilentlyOverwritten(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, content);
        Assert.Throws<JsonException>(() => new SessionStore(StorePath).Load());
        Assert.Equal(content, File.ReadAllText(StorePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
