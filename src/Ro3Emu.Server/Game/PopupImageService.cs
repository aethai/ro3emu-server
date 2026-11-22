using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 83271 POPUPIMAGE: the client asks which images its notice popup should show (PopupImageReq {language, channel}) and
/// the original server answered with the campaign banners. Nothing answered it here, so every request got the generic
/// empty reply and the popup stayed empty (114 in one live server log).
/// <para>
/// The banners are a GM-editable file next to the realm state (<c>data/popup-images.json</c>), re-read whenever it
/// changes, so a campaign needs no rebuild. A missing file, or an unreadable one, answers with no images: the popup
/// then shows nothing, as it does today.
/// </para>
/// <code>
/// [
///   {
///     "id": 1,
///     "title": "Season Pass on sale",
///     "imageUrl": "https://ragnarok.ngrok-free.dev/brand/emblem.png",
///     "language": "en",
///     "channel": "TBT_HMT_PC",
///     "startTime": "2026-11-20 00:00:00",
///     "rank": 1
///   }
/// ]
/// </code>
/// <see cref="Banner.Language"/> and <see cref="Banner.Channel"/> empty mean every client; a filled one must match the
/// request (the client sends "en" and "TBT_HMT_PC"). <see cref="Banner.StartTime"/> is shown as sent, the client's own
/// date field: no scheduling is inferred from it. <see cref="Banner.JumpFunctionId"/> and
/// <see cref="Banner.JumpPanel"/> are the client's jump targets behind the image; a jump id it cannot resolve shows an
/// empty panel, so both stay empty unless a campaign names one.
/// </summary>
public sealed class PopupImageService(IOptions<ServerOptions> options, ILogger<PopupImageService> log)
{
    /// <summary>The banner file, in the realm's <c>DataDir</c> like <c>msgtable.tsv</c>.</summary>
    internal const string FileName = "popup-images.json";

    /// <summary>One banner row of the file.</summary>
    private sealed record Banner(long Id, string Title, string ImageUrl, string Language, string Channel,
        string StartTime, int Rank, string ExJump, string JumpFunctionId, string JumpPanel);

    /// <summary>The file's shape: unknown members are ignored, so a comment field may be added by hand.</summary>
    private sealed record Row
    {
        public long Id { get; init; }
        public string? Title { get; init; }
        public string? ImageUrl { get; init; }
        public string? Language { get; init; }
        public string? Channel { get; init; }
        public string? StartTime { get; init; }
        public int Rank { get; init; }
        public string? ExJump { get; init; }
        public string? JumpFunctionId { get; init; }
        public string? JumpPanel { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private readonly object _gate = new();
    private List<Banner> _banners = [];
    private long _ticks, _length;

    private string PathOf => Path.Combine(options.Value.DataDir, FileName);

    /// <summary>The banners this client asks for, in rank order (rank, then id: the client shows the first).</summary>
    public PopupImageRes Images(PopupImageReq request)
    {
        var response = new PopupImageRes();
        foreach (var banner in Current()
            .Where(b => Accepts(b.Language, request.Language) && Accepts(b.Channel, request.Channel))
            .OrderBy(b => b.Rank).ThenBy(b => b.Id))
        {
            response.Images.Add(new PopupImageResInfo
            {
                PopImageId = banner.Id,
                Title = banner.Title,
                Rank = banner.Rank,
                StartTime = banner.StartTime,
                ImageInfo = new PopupImage
                {
                    ImageUrl = banner.ImageUrl,
                    Language = banner.Language,
                    ExJump = banner.ExJump,
                    InternalJumpFunctionId = banner.JumpFunctionId,
                    InternalJumpPanel = banner.JumpPanel,
                },
            });
        }
        return response;
    }

    /// <summary>Empty in the file means every client; a filled value must equal what the request carries.</summary>
    private static bool Accepts(string configured, string requested) =>
        configured.Length == 0 || requested.Length == 0 || string.Equals(configured, requested, StringComparison.OrdinalIgnoreCase);

    private List<Banner> Current()
    {
        try
        {
            var file = new FileInfo(PathOf);
            if (!file.Exists) return [];
            lock (_gate)
            {
                if (file.LastWriteTimeUtc.Ticks == _ticks && file.Length == _length) return _banners;
                _banners = Parse(file);
                _ticks = file.LastWriteTimeUtc.Ticks;
                _length = file.Length;
                return _banners;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A campaign file that cannot be read must not break the client's login: it just shows no popup.
            log.LogWarning("Popup images {Path}: {Message}", PathOf, e.Message);
            return [];
        }
    }

    private List<Banner> Parse(FileInfo file)
    {
        using var stream = file.OpenRead();
        var rows = JsonSerializer.Deserialize<List<Row>>(stream, Json) ?? [];
        return [.. rows
            .Where(r => !string.IsNullOrWhiteSpace(r.ImageUrl))
            .Select((r, index) => new Banner(
                r.Id != 0 ? r.Id : index + 1,
                r.Title ?? "",
                r.ImageUrl!.Trim(),
                r.Language?.Trim() ?? "",
                r.Channel?.Trim() ?? "",
                r.StartTime?.Trim() ?? "",
                r.Rank,
                r.ExJump?.Trim() ?? "",
                r.JumpFunctionId?.Trim() ?? "",
                r.JumpPanel?.Trim() ?? ""))];
    }
}
