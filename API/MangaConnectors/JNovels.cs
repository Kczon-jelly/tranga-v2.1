using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using API.MangaDownloadClients;
using API.Schema.MangaContext;

namespace API.MangaConnectors;

/// <summary>
/// Connector for jnovels.com - a WordPress blog where each post is one Volume of a Light Novel,
/// with the actual download hidden behind a charexempire.com link-locker.
///
/// IMPORTANT - this connector does NOT auto-resolve the link-locker. Link-lockers exist specifically
/// to force a human to view an ad/wait a timer before revealing the real file link, and this one's
/// robots.txt explicitly disallows automated access. Bypassing that is both unreliable (they change
/// their tricks often) and a different category of scraping than a normal scanlation site.
///
/// Instead: each "Chapter" (Volume) downloads as a placeholder .epub containing the direct
/// charexempire.com link for you to click through yourself in a browser. This is a deliberate
/// design choice, not a limitation to "fix" later - see conversation history / README notes.
/// </summary>
public class JNovels : MangaConnector
{
    public JNovels() : base("JNovels", ["en"], ["jnovels.com"], "https://jnovels.com/wp-content/uploads/2018/05/cropped-JNOVEL.png", MediaType.LightNovel)
    {
        this.downloadClient = new HttpDownloadClient();
    }

    // jnovels has no per-series index page - every post is "{Title} Volume N {Pdf|Epub}".
    // We use the WordPress search endpoint and group same-title posts into one series.
    private static readonly Regex VolumeSuffixRex = new(
        @"\s+volume\s+(?<vol>\d+)\s+(?:Light\s+Novel\s+)?(?<fmt>Pdf|Epub)\s*$", RegexOptions.IgnoreCase);
    // Some posts (rarer) use "Light Novel Pdf/Epub" without a volume number (single-volume or omnibus)
    private static readonly Regex NoVolumeSuffixRex = new(
        @"\s+Light\s+Novel\s+(?<fmt>Pdf|Epub)\s*$", RegexOptions.IgnoreCase);

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching: {0}", mangaSearchName);
        var posts = SearchPosts(mangaSearchName);
        if (posts.Count == 0)
            return [];

        // Group posts by their series title (post title minus the "Volume N Pdf/Epub" suffix)
        var bySeries = posts.GroupBy(p => p.SeriesTitle, StringComparer.OrdinalIgnoreCase);

        List<(Manga, MangaConnectorId<Manga>)> results = new();
        foreach (var group in bySeries)
        {
            string seriesTitle = group.Key;
            string seriesId = HttpUtility.UrlEncode(seriesTitle);
            // Cover: use the first post's cover image
            string coverUrl = group.First().CoverUrl;
            List<Link> links = [new Link("JNovels", $"https://jnovels.com/?s={seriesId}")];

            Manga manga = new(seriesTitle, "Light Novel from jnovels.com. Chapters are placeholder links " +
                "to a download-locker page - open the file in your browser and download it manually.",
                coverUrl, MangaReleaseStatus.Continuing, [], [], links, [], mediaType: MediaType.LightNovel);
            MangaConnectorId<Manga> mangaId = new(manga, this, seriesId, links[0].LinkUrl);
            manga.MangaConnectorIds.Add(mangaId);
            results.Add((manga, mangaId));
        }

        Log.InfoFormat("Search '{0}' yielded {1} series ({2} posts).", mangaSearchName, results.Count, posts.Count);
        return results.ToArray();
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        // jnovels posts are individual volumes, not series pages - if a user pastes a specific post URL,
        // treat its series title (derived from the post title) as the id and look it up normally.
        HttpResponseMessage response = downloadClient.MakeRequest(url, RequestType.MangaInfo).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            return null;
        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);
        HtmlNode? titleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
        string rawTitle = titleNode?.GetAttributeValue("content", "") ?? "";
        string seriesTitle = StripVolumeSuffix(HtmlEntity.DeEntitize(rawTitle));
        return seriesTitle.Length > 0 ? GetMangaFromId(HttpUtility.UrlEncode(seriesTitle)) : null;
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        string seriesTitle = HttpUtility.UrlDecode(mangaIdOnSite);
        (Manga, MangaConnectorId<Manga>)[] results = SearchManga(seriesTitle);
        return results.FirstOrDefault(r => r.Item1.Name.Equals(seriesTitle, StringComparison.OrdinalIgnoreCase));
    }

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId, string? language = null)
    {
        string seriesTitle = HttpUtility.UrlDecode(mangaId.IdOnConnectorSite);
        var posts = SearchPosts(seriesTitle).Where(p => p.SeriesTitle.Equals(seriesTitle, StringComparison.OrdinalIgnoreCase));

        // Prefer the Epub-format post over Pdf when both exist for the same volume number
        var byVolume = posts.GroupBy(p => p.VolumeNumber);

        List<(Chapter, MangaConnectorId<Chapter>)> chapters = new();
        foreach (var group in byVolume)
        {
            JNovelsPost chosen = group.FirstOrDefault(p => p.Format.Equals("Epub", StringComparison.OrdinalIgnoreCase)) ?? group.First();
            string chapterNumber = chosen.VolumeNumber?.ToString() ?? (chapters.Count + 1).ToString();

            Chapter ch;
            try
            {
                ch = new Chapter(mangaId.Obj, chapterNumber, null, chosen.Format);
            }
            catch (ArgumentException)
            {
                continue;
            }
            // We stash the post URL as the WebsiteUrl; GetChapterText re-fetches it to find the locker link
            MangaConnectorId<Chapter> mcId = new(ch, this, chosen.PostUrl, chosen.PostUrl);
            ch.MangaConnectorIds.Add(mcId);
            chapters.Add((ch, mcId));
        }

        Log.InfoFormat("Found {0} volumes for {1}", chapters.Count, mangaId.Obj.Name);
        return chapters.OrderBy(c => c.Item1, new Chapter.ChapterComparer()).ToArray();
    }

    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId) => [];

    internal override string? GetChapterText(MangaConnectorId<Chapter> chapterId)
    {
        string? postUrl = chapterId.WebsiteUrl;
        if (postUrl is null)
            return null;

        HttpResponseMessage response = downloadClient.MakeRequest(postUrl, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to load post page");
            return null;
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // The "VOLUME N" link on the post points at charexempire.com (the link-locker)
        HtmlNode? lockerLink = doc.DocumentNode.SelectSingleNode("//a[contains(@href,'charexempire.com')]");
        string lockerUrl = lockerLink?.GetAttributeValue("href", "") ?? postUrl;

        StringBuilder sb = new();
        sb.Append("<p><strong>This is a placeholder, not the actual novel content.</strong></p>\n");
        sb.Append("<p>jnovels.com hides its real download link behind a link-locker page, which this app ")
          .Append("deliberately does not try to auto-bypass (see Tranga's JNovels connector notes).</p>\n");
        sb.Append($"<p>Open this link in your browser to get the real file: <a href=\"{System.Net.WebUtility.HtmlEncode(lockerUrl)}\">{System.Net.WebUtility.HtmlEncode(lockerUrl)}</a></p>\n");
        sb.Append($"<p>Original post: <a href=\"{System.Net.WebUtility.HtmlEncode(postUrl)}\">{System.Net.WebUtility.HtmlEncode(postUrl)}</a></p>\n");

        return sb.ToString();
    }

    private record JNovelsPost(string SeriesTitle, int? VolumeNumber, string Format, string PostUrl, string CoverUrl);

    private List<JNovelsPost> SearchPosts(string query)
    {
        string requestUrl = $"https://jnovels.com/?s={HttpUtility.UrlEncode(query)}";
        HttpResponseMessage response = downloadClient.MakeRequest(requestUrl, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Search request failed");
            return [];
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Each search result is an <article>/post block with an <h1>/<h2> title link and a cover <img>
        HtmlNodeCollection? titleNodes = doc.DocumentNode.SelectNodes("//h1/a[contains(@href,'jnovels.com/')] | //h2/a[contains(@href,'jnovels.com/')]");
        if (titleNodes is null)
            return [];

        List<JNovelsPost> posts = new();
        foreach (HtmlNode titleNode in titleNodes)
        {
            string postUrl = titleNode.GetAttributeValue("href", "").Trim();
            string postTitle = HtmlEntity.DeEntitize(titleNode.InnerText.Trim());

            Match volMatch = VolumeSuffixRex.Match(postTitle);
            string seriesTitle;
            int? volumeNumber;
            string format;
            if (volMatch.Success)
            {
                seriesTitle = postTitle[..volMatch.Index].Trim();
                volumeNumber = int.Parse(volMatch.Groups["vol"].Value);
                format = volMatch.Groups["fmt"].Value;
            }
            else
            {
                Match noVolMatch = NoVolumeSuffixRex.Match(postTitle);
                if (!noVolMatch.Success)
                    continue; // not a recognizable "{Title} Volume N Pdf/Epub" post - skip (e.g. manga/WN posts use a different naming scheme)
                seriesTitle = postTitle[..noVolMatch.Index].Trim();
                volumeNumber = null;
                format = noVolMatch.Groups["fmt"].Value;
            }

            // Cover image: nearest <img> that appears right after this title link in the same post block
            HtmlNode? imgNode = titleNode.SelectSingleNode("../../..//img[1]") ?? titleNode.SelectSingleNode("../..//img[1]");
            string coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

            posts.Add(new JNovelsPost(seriesTitle, volumeNumber, format, postUrl, coverUrl));
        }

        return posts;
    }

    private static string StripVolumeSuffix(string postTitle)
    {
        Match m = VolumeSuffixRex.Match(postTitle);
        if (m.Success)
            return postTitle[..m.Index].Trim();
        Match m2 = NoVolumeSuffixRex.Match(postTitle);
        return m2.Success ? postTitle[..m2.Index].Trim() : "";
    }
}
