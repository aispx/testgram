using MyTelegram.Messenger.Services.HistoryImport;

namespace MyTelegram.Messenger.Tests.HistoryImport;

/// <summary>
/// Feature: imported messages.
///
/// <para>
/// The server is what reads the chat export file: <c>messages.checkHistoryImport</c> answers from the
/// first 100 lines the client sends, and <c>messages.initHistoryImport</c> has to turn the whole file
/// into messages. Every supported app writes its own dialect, in the locale of the phone that produced
/// the file, so these tests pin the shapes seen in real exports.
/// See https://corefork.telegram.org/api/import
/// </para>
/// </summary>
public class ChatExportParserTests
{
    private readonly ChatExportParser _parser = new();

    private const string WhatsAppAndroid = """
        12/31/20, 11:58 PM - Messages and calls are end-to-end encrypted.
        12/31/20, 11:59 PM - John Doe: Happy new year!
        12/31/20, 11:59 PM - Jane: Same to you
        1/1/21, 00:05 AM - John Doe: IMG-20210101-WA0001.jpg (file attached)
        1/1/21, 00:06 AM - Jane: <Media omitted>
        """;

    private const string WhatsAppIos = """
        [31/12/2020, 23:59:01] John Doe: Happy new year!
        [31/12/2020, 23:59:20] Jane: Same to you
        and to your family
        [01/01/2021, 00:05:00] John Doe: ‎<attached: 00000042-PHOTO-2021-01-01-00-05-00.jpg>
        """;

    private const string LineExport = """
        [LINE] Chat history with John Doe
        Saved on: 2020/12/31 23:59

        2020/12/31(Thu)
        23:59	John Doe	Happy new year!
        23:59	Jane	Same to you
        2021/01/01(Fri)
        00:05	John Doe	[File] report.pdf
        00:06	Jane	[Photo]
        """;

    private const string KakaoTalkEnglish = """
        Chat with John Doe
        Date Saved : 2020-12-31 23:59:59

        --------------- Thursday, December 31, 2020 ---------------
        [John Doe] [11:59 PM] Happy new year!
        [Jane] [11:59 PM] Same to you
        --------------- Friday, January 1, 2021 ---------------
        [John Doe] [12:05 AM] File: report.pdf
        [Jane] [12:06 AM] Photo
        """;

    private const string KakaoTalkKorean = """
        John Doe 님과 카카오톡 대화
        저장한 날짜 : 2020-12-31 23:59:59

        2020년 12월 31일 오후 11:59, John Doe : Happy new year!
        2021년 1월 1일 오전 12:05, Jane : Same to you
        """;

    [Fact]
    public void A_WhatsApp_Android_export_is_recognized()
    {
        var head = _parser.ParseHead(WhatsAppAndroid);

        head.ShouldNotBeNull();
        head!.Format.ShouldBe(ChatExportFormat.WhatsApp);
    }

    [Fact]
    public void A_WhatsApp_Android_export_keeps_the_original_authors_and_dates()
    {
        var result = _parser.Parse(WhatsAppAndroid)!;

        result.Messages.Count.ShouldBe(4);
        result.Messages[0].FromName.ShouldBe("John Doe");
        result.Messages[0].Text.ShouldBe("Happy new year!");

        // 12/31/20, 11:59 PM read as a US date: 2020-12-31 23:59 UTC.
        result.Messages[0].Date.ShouldBe(1609459140);
        result.Messages[1].FromName.ShouldBe("Jane");
    }

    [Fact]
    public void The_encryption_notice_of_WhatsApp_is_not_imported_as_a_message()
    {
        var result = _parser.Parse(WhatsAppAndroid)!;

        result.Messages.ShouldAllBe(p => !p.Text.Contains("end-to-end"));
    }

    [Fact]
    public void An_attached_file_of_a_WhatsApp_Android_export_is_named()
    {
        var result = _parser.Parse(WhatsAppAndroid)!;

        var attachment = result.Messages.Single(p => p.FileName != null);
        attachment.FileName.ShouldBe("IMG-20210101-WA0001.jpg");
        attachment.Text.ShouldBeEmpty();
    }

    [Fact]
    public void A_placeholder_left_by_an_export_without_media_is_imported_verbatim()
    {
        var result = _parser.Parse(WhatsAppAndroid)!;

        // The official server imports the placeholder as plain text — a production import shows
        // "<Без медиафайлов>" as the message itself — and its wording differs per language, so it
        // cannot be recognized and dropped anyway.
        result.Messages[^1].Text.ShouldBe("<Media omitted>");
    }

    [Fact]
    public void A_WhatsApp_iOS_export_is_read_with_day_first_dates()
    {
        var result = _parser.Parse(WhatsAppIos)!;

        result.Format.ShouldBe(ChatExportFormat.WhatsApp);
        result.Messages.Count.ShouldBe(3);
        // 31/12/2020 23:59:01 UTC
        result.Messages[0].Date.ShouldBe(1609459141);
    }

    [Fact]
    public void A_message_spanning_several_lines_is_kept_whole()
    {
        var result = _parser.Parse(WhatsAppIos)!;

        result.Messages[1].Text.ShouldBe("Same to you\nand to your family");
    }

    [Fact]
    public void The_bidi_marks_of_an_iOS_export_do_not_hide_the_attachment()
    {
        var result = _parser.Parse(WhatsAppIos)!;

        result.Messages[2].FileName.ShouldBe("00000042-PHOTO-2021-01-01-00-05-00.jpg");
    }

    [Fact]
    public void A_date_written_day_first_is_detected_even_in_the_dash_flavour()
    {
        // 25 cannot be a month, so the whole file is read as day/month/year.
        var result = _parser.Parse("25/12/20, 10:00 - John: Merry Christmas")!;

        var expected = new DateTimeOffset(new DateTime(2020, 12, 25, 10, 0, 0, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        result.Messages.Single().Date.ShouldBe((int)expected);
    }

    [Fact]
    public void A_group_export_is_reported_as_a_group_with_its_title()
    {
        const string export = """
            12/31/20, 11:58 PM - John Doe created group "Family"
            12/31/20, 11:59 PM - John Doe: Happy new year!
            """;

        var head = _parser.ParseHead(export)!;

        head.IsGroup.ShouldBeTrue();
        head.IsPm.ShouldBeFalse();
        head.Title.ShouldBe("Family");
    }

    [Fact]
    public void An_export_with_three_participants_is_reported_as_a_group()
    {
        const string export = """
            12/31/20, 11:59 PM - John: one
            12/31/20, 11:59 PM - Jane: two
            12/31/20, 11:59 PM - Bob: three
            """;

        _parser.ParseHead(export)!.IsGroup.ShouldBeTrue();
    }

    [Fact]
    public void A_two_party_export_is_reported_as_a_private_chat()
    {
        _parser.ParseHead(WhatsAppAndroid)!.IsPm.ShouldBeTrue();
    }

    [Fact]
    public void A_LINE_export_is_recognized_and_titled()
    {
        var head = _parser.ParseHead(LineExport)!;

        head.Format.ShouldBe(ChatExportFormat.Line);
        head.Title.ShouldBe("John Doe");
        head.IsPm.ShouldBeTrue();
    }

    [Fact]
    public void A_LINE_export_dates_its_messages_from_the_day_block()
    {
        var result = _parser.Parse(LineExport)!;

        result.Messages.Count.ShouldBe(4);
        result.Messages[0].Date.ShouldBe(1609459140);
        result.Messages[0].FromName.ShouldBe("John Doe");
        result.Messages[0].Text.ShouldBe("Happy new year!");

        // 2021/01/01 00:05 UTC, the day block moved on.
        result.Messages[2].Date.ShouldBe(1609459500);
    }

    [Fact]
    public void A_LINE_attachment_token_names_the_file_when_the_export_does()
    {
        var result = _parser.Parse(LineExport)!;

        result.Messages[2].FileName.ShouldBe("report.pdf");
        result.Messages[2].Text.ShouldBeEmpty();

        // A bare "[Photo]" names no file, so it is imported as the text it is.
        result.Messages[3].FileName.ShouldBeNull();
        result.Messages[3].Text.ShouldBe("[Photo]");
    }

    [Fact]
    public void A_KakaoTalk_export_is_recognized_and_titled()
    {
        var head = _parser.ParseHead(KakaoTalkEnglish)!;

        head.Format.ShouldBe(ChatExportFormat.KakaoTalk);
        head.Title.ShouldBe("John Doe");
    }

    [Fact]
    public void A_KakaoTalk_export_dates_its_messages_from_the_day_separator()
    {
        var result = _parser.Parse(KakaoTalkEnglish)!;

        result.Messages.Count.ShouldBe(4);
        result.Messages[0].Date.ShouldBe(1609459140);
        result.Messages[2].FileName.ShouldBe("report.pdf");
        result.Messages[2].Date.ShouldBe(1609459500);

        // A bare "Photo" names no file, so it is imported as the text it is.
        result.Messages[3].FileName.ShouldBeNull();
        result.Messages[3].Text.ShouldBe("Photo");
    }

    [Fact]
    public void The_Korean_desktop_form_of_KakaoTalk_is_read_too()
    {
        var result = _parser.Parse(KakaoTalkKorean)!;

        result.Format.ShouldBe(ChatExportFormat.KakaoTalk);
        result.Messages.Count.ShouldBe(2);
        result.Messages[0].FromName.ShouldBe("John Doe");
        result.Messages[0].Date.ShouldBe(1609459140);
        // 오전 12:05 is 00:05, not 12:05.
        result.Messages[1].Date.ShouldBe(1609459500);
    }

    [Fact]
    public void A_file_from_an_unsupported_app_is_not_recognized()
    {
        _parser.ParseHead("Dear diary,\nnothing happened today.\n").ShouldBeNull();
        _parser.Parse("Dear diary,\nnothing happened today.\n").ShouldBeNull();
    }

    [Fact]
    public void An_empty_head_is_not_recognized()
    {
        _parser.ParseHead(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void An_impossible_date_stops_the_import()
    {
        // 31/02 does not exist: the file has to be rejected with IMPORT_FORMAT_DATE_INVALID.
        Should.Throw<ChatExportDateException>(() => _parser.Parse("31/02/2020, 10:00 - John: hello"));
    }

    [Fact]
    public void A_truncated_head_with_a_broken_date_still_answers_checkHistoryImport()
    {
        // The head is the first 100 lines of a file, so its last line can be anything.
        var head = _parser.ParseHead("""
            12/31/20, 11:59 PM - John: fine
            31/02/2020, 10:00 - John: broken
            """);

        head.ShouldNotBeNull();
        head!.Format.ShouldBe(ChatExportFormat.WhatsApp);
    }
}
