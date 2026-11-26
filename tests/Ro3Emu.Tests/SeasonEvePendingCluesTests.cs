using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// PlayerSeasonEveChapterInfo.clues is the client's kPendingClueMap: every clue of a chapter's _kClueList, false while
/// pending and true once found (AdventureActivityService.SeedEvePendingClues). An empty map left the Goddess's Hint
/// dialogue without a city name. Synthetic data: chapter 7 lists clues 71-73, chapter 8 lists clue 81.
/// </summary>
public sealed class SeasonEvePendingCluesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-eve-clues-" + Guid.NewGuid().ToString("N"));
    private readonly GameData _data;
    private static readonly int[] FirstClues = [71, 72, 73];
    private static readonly int[] SecondClues = [81];

    public SeasonEvePendingCluesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "SeasonEveChapterConfig.json"), JsonSerializer.Serialize(new[]
        {
            new { _iId = 7, _iClueCount = 2, _iNoteTypeCount = 3, _kClueList = FirstClues },
            new { _iId = 8, _iClueCount = 1, _iNoteTypeCount = 3, _kClueList = SecondClues },
        }));
        _data = new GameData(Options.Create(new ServerOptions { GameDataDir = _root }), NullLogger<GameData>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void ANewStateListsEveryChapterClueAsPending()
    {
        var state = new PlayerActivitySeasonEve();
        AdventureActivityService.SeedEvePendingClues(_data, state);

        Assert.Equal([7L, 8L], state.ChapterInfo.Keys.Order());
        Assert.Equal(new Dictionary<long, bool> { [71] = false, [72] = false, [73] = false }, new Dictionary<long, bool>(state.ChapterInfo[7].Clues));
        Assert.Equal(new Dictionary<long, bool> { [81] = false }, new Dictionary<long, bool>(state.ChapterInfo[8].Clues));
    }

    [Fact]
    public void ASavedStateKeepsFoundCluesAndGainsTheMissingPendingOnes()
    {
        var state = new PlayerActivitySeasonEve();
        state.ChapterInfo[7] = new PlayerSeasonEveChapterInfo { IsFinish = true, Clues = { [72] = true }, Notes = { [2] = 2 } };

        AdventureActivityService.SeedEvePendingClues(_data, state);
        AdventureActivityService.SeedEvePendingClues(_data, state);

        var chapter = state.ChapterInfo[7];
        Assert.True(chapter.IsFinish);
        Assert.Equal(2, chapter.Notes[2]);
        Assert.Equal(new Dictionary<long, bool> { [71] = false, [72] = true, [73] = false }, new Dictionary<long, bool>(chapter.Clues));
        Assert.Equal(new Dictionary<long, bool> { [81] = false }, new Dictionary<long, bool>(state.ChapterInfo[8].Clues));
    }
}
