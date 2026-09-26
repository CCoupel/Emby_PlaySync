using EmbySharedPlaylist.Spike;
using MediaBrowser.Model.Logging;
using Moq;
using Xunit;

namespace EmbySharedPlaylist.Tests;

public class SpikeLogTests
{
    [Fact]
    public void Info_WritesToFileLoggerAndToConsoleWithShortPrefix()
    {
        var logger = new Mock<ILogger>();
        var console = new StringWriter();
        new SpikeLog(logger.Object, console).Info("EmbySharedPlaylist spike : ItemUpdated playlist=7 reason=MetadataEdit");

        logger.Verify(l => l.Info("{0}", It.Is<object[]>(a => (string)a[0] == "EmbySharedPlaylist spike : ItemUpdated playlist=7 reason=MetadataEdit")), Times.Once);
        Assert.Equal("[EmbySharedPlaylist] ItemUpdated playlist=7 reason=MetadataEdit" + Environment.NewLine, console.ToString());
    }

    [Fact]
    public void Debug_GoesToTheFileOnly_NeverToTheConsole()
    {
        var logger = new Mock<ILogger>();
        var console = new StringWriter();
        new SpikeLog(logger.Object, console).Debug("EmbySharedPlaylist spike : UserDataSaved user=a item=1 reason=PlaybackProgress played=false pos=5 pluginWrite=false");

        logger.Verify(l => l.Debug("{0}", It.IsAny<object[]>()), Times.Once);
        Assert.Equal(string.Empty, console.ToString());
    }

    [Fact]
    public void Error_ConsoleShowsOnlyTheExceptionType_FileGetsTheException()
    {
        var logger = new Mock<ILogger>();
        var console = new StringWriter();
        var ex = new IOException(@"impossible d'ouvrir /config/secret/chemin.txt");
        new SpikeLog(logger.Object, console).Error("EmbySharedPlaylist : erreur dans UserDataSaved", ex);

        logger.Verify(l => l.ErrorException("{0}", ex, It.IsAny<object[]>()), Times.Once);
        var text = console.ToString();
        Assert.Equal("[EmbySharedPlaylist] ERROR erreur dans UserDataSaved (IOException)" + Environment.NewLine, text);
        Assert.DoesNotContain("chemin", text);
    }

    [Fact]
    public void FailingLoggerAndFailingConsole_NeverThrow()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.Info(It.IsAny<string>(), It.IsAny<object[]>())).Throws<InvalidOperationException>();
        logger.Setup(l => l.Debug(It.IsAny<string>(), It.IsAny<object[]>())).Throws<InvalidOperationException>();
        logger.Setup(l => l.ErrorException(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<object[]>())).Throws<InvalidOperationException>();
        var log = new SpikeLog(logger.Object, new ThrowingWriter());

        var ex = Record.Exception(() => { log.Info("x"); log.Debug("x"); log.Error("x", new Exception("e")); });
        Assert.Null(ex);
    }

    [Fact]
    public void NullLogger_StillWritesTheConsole()
    {
        var console = new StringWriter();
        new SpikeLog(null, console).Info(SpikeLogFormat.Startup(true));
        Assert.Equal("[EmbySharedPlaylist] écouteurs du spike enregistrés (EnableSpikeEndpoints=true)" + Environment.NewLine, console.ToString());
    }

    [Fact]
    public void ConfigSaved_ReflectsTheCurrentValue_OnBothChannels()
    {
        var console = new StringWriter();
        var log = new SpikeLog(null, console);
        log.Info(SpikeLogFormat.ConfigSaved(true));
        log.Info(SpikeLogFormat.ConfigSaved(false));
        Assert.Equal(new[]
        {
            "[EmbySharedPlaylist] configuration enregistrée (EnableSpikeEndpoints=true)",
            "[EmbySharedPlaylist] configuration enregistrée (EnableSpikeEndpoints=false)"
        }, console.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("EmbySharedPlaylist spike : Setup playlist=1 owner=g members=2", "[EmbySharedPlaylist] Setup playlist=1 owner=g members=2")]
    [InlineData("EmbySharedPlaylist : configuration enregistrée (EnableSpikeEndpoints=true)", "[EmbySharedPlaylist] configuration enregistrée (EnableSpikeEndpoints=true)")]
    [InlineData("autre ligne", "[EmbySharedPlaylist] autre ligne")]
    public void ToConsole_ReplacesTheFilePrefixWithTheShortOne(string line, string expected) =>
        Assert.Equal(expected, SpikeLogFormat.ToConsole(line));

    [Fact]
    public void ToConsole_ErrorCarriesErrorAndExceptionType() =>
        Assert.Equal("[EmbySharedPlaylist] ERROR SpikeSetup (ArgumentException)",
            SpikeLogFormat.ToConsole("EmbySharedPlaylist spike : SpikeSetup", isError: true, exceptionType: "ArgumentException"));

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("console fermée");
    }
}
