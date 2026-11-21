using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class MailLegacyParamsTests
{
    private static byte[] Params(params LanguageParam[] values)
    {
        var data = new LanguageData();
        data.Param.AddRange(values);
        return data.ToByteArray();
    }
    private static LanguageParam Int(long value) => new() { Tp = LanguageParamType.Int, IntParam = value };
    private static LanguageParam Str(string value) => new() { Tp = LanguageParamType.Str, StrParam = value };

    [Fact]
    public void AwardLettersWrittenWithStringParametersAreSentWithoutThem()
    {
        // In-game 11.10: pre-fix award letters logged "id:0 is not in AuctionTabConfig" on every mail sync.
        var legacy = new MailService.Letter { TemplateId = 1042, ContentParams = Params(Int(500), Str("Zeny"), Str("Guild"), Str("Sword")) };
        var current = new MailService.Letter { TemplateId = 1045, ContentParams = Params(Int(500), Int(1), Int(1003), Int(2090450)) };
        var other = new MailService.Letter { TemplateId = 1040, ContentParams = Params(Str("Sword")) };
        Assert.True(MailService.LegacyAwardParams(legacy));
        Assert.False(MailService.LegacyAwardParams(current));
        Assert.False(MailService.LegacyAwardParams(other));
    }

    [Fact]
    public void StallSaleLettersWrittenAsStringsAreSentWithoutParametersAndNewOnesKeepTheirRecord()
    {
        // #479: the client reads 1029 as a StallsRecord; an old LanguageData letter hid the row and its zeny.
        var legacy = new MailService.Letter
        {
            TemplateId = 1029, ContentParams = Params(Str("Sword"), Str("1"), Str("Buyer"),
            Str("2026-11-20 10:00:00"), Str("900"), Str("9100"))
        };
        var current = new MailService.Letter
        {
            TemplateId = 1029, ContentParams = new StallsRecord
            { ItemEntryID = 1000100, ItemNum = 1, Buytime = 1_795_000_000, Tradetax = 900, Gainmoney = 9100, Buyerid = 5, Buyername = "Buyer", CoinEntryID = 2 }
            .ToByteArray()
        };
        Assert.True(MailService.LegacyAwardParams(legacy));
        Assert.False(MailService.LegacyAwardParams(current));
    }
}
