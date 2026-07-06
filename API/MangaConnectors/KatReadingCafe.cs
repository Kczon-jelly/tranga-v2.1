using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using API.MangaDownloadClients;
using API.Schema.MangaContext;

namespace API.MangaConnectors;

/// <summary>
/// Connector for katreadingcafe.com - a WordPress "Madara" theme Web Novel site behind Cloudflare
/// bot-protection. Uses ChromiumDownloadClient (headless browser) for every request, since a plain
/// HTTP client gets blocked by the Cloudflare challenge before it ever sees the page.
///
/// IMPORTANT: The exact markup below follows well-known Madara-theme conventions (used by many
/// manga/novel sites), but could not be verified against a live fetch because Cloudflare blocks
/// automated access even for a one-off inspection request. Treat the selectors here as a strong
/// starting point that will likely need small adjustments after your first real test run - check
/// the debug logs (which log an HTML snippet on parse failure) if chapters/text aren't coming through.
/// </summary>
public class KatReadingCafe : MangaConnector
{
    public KatReadingCafe() : base("KatReadingCafe", ["en"], ["katreadingcafe.com"], "https://katreadingcafe.com/wp-content/uploads/favicon.ico", MediaType.WebNovel)
    {
        // Every request goes through Chromium since plain HTTP gets Cloudflare-blocked immediately.
        this.downloadClient = new ChromiumDownloadClient();
    }

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching: {0}", mangaSearchName);
        // Madara-theme search endpoint
        string requestUrl = $"https://katreadingcafe.com/?s={HttpUtility.UrlEncode(mangaSearchName)}&post_type=wp-manga";
        HttpResponseMessage response = downloadClient.MakeRequest(requestUrl, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Search request failed");
            return [];
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Madara search results: each result is an <a> to /series/{slug}/
        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'/series/')]");
        if (nodes is null)
        {
            Log.Info("No results found");
            return [];
        }

        Regex seriesUrlRex = new(@"^https?://katreadingcafe\.com/series/([a-z0-9-]+)/?$");
        HashSet<string> seenSlugs = new();
        List<(Manga, MangaConnectorId<Manga>)> results = new();
        foreach (HtmlNode node in nodes)
        {
            string href = node.GetAttributeValue("href", "").Trim();
            Match m = seriesUrlRex.Match(href);
            if (!m.Success || !seenSlugs.Add(m.Groups[1].Value))
                continue;

            if (GetMangaFromId(m.Groups[1].Value) is { } manga)
                results.Add(manga);
        }

        Log.InfoFormat("Search '{0}' yielded {1} results.", mangaSearchName, results.Count);
        return results.DistinctBy(r => r.Item1.Key).ToArray();
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        Match m = Regex.Match(url, @"^https?://(?:www\.)?katreadingcafe\.com/series/([a-z0-9-]+)/?");
        return m.Success ? GetMangaFromId(m.Groups[1].Value) : null;
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        string url = $"https://katreadingcafe.com/series/{mangaIdOnSite}/";
        HttpResponseMessage response = downloadClient.MakeRequest(url, RequestType.MangaInfo).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to retrieve series page");
            return null;
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Madara: title in .post-title h1 (sometimes wraps a status <span> that needs stripping)
        HtmlNode? titleNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'post-title')]//h1");
        string title = titleNode is not null
            ? HtmlEntity.DeEntitize(Regex.Replace(titleNode.InnerText, @"\s+", " ").Trim())
            : mangaIdOnSite;

        // Madara: cover in .summary_image img (data-src for lazy-loaded images, fallback to src)
        HtmlNode? coverNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'summary_image')]//img");
        string coverUrl = coverNode?.GetAttributeValue("data-src", "") ?? "";
        if (string.IsNullOrEmpty(coverUrl))
            coverUrl = coverNode?.GetAttributeValue("src", "") ?? "";

        // Madara: description in .summary__content or .description-summary
        HtmlNode? descNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'summary__content')]") ??
                              doc.DocumentNode.SelectSingleNode("//div[contains(@class,'description-summary')]");
        string description = descNode is not null ? HtmlEntity.DeEntitize(descNode.InnerText.Trim()) : "";

        // Madara: genres/tags in .genres-content a
        HtmlNodeCollection? tagNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'genres-content')]//a");
        List<MangaTag> tags = tagNodes?
            .Select(n => HtmlEntity.DeEntitize(n.InnerText.Trim()))
            .Where(t => t.Length > 0)
            .Distinct()
            .Select(t => new MangaTag(t))
            .ToList() ?? [];

        // Madara: author in .author-content a
        List<Author> authors = [];
        HtmlNodeCollection? authorNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'author-content')]//a");
        if (authorNodes is not null)
            authors.AddRange(authorNodes.Select(n => new Author(HtmlEntity.DeEntitize(n.InnerText.Trim()))));

        // Madara: release status in .post-status .summary-content (text like "OnGoing"/"Completed")
        HtmlNode? statusNode = doc.DocumentNode.SelectSingleNode(
            "//div[contains(@class,'post-status')]//div[contains(@class,'summary-content')]");
        string rawStatus = statusNode?.InnerText.Trim().ToLowerInvariant() ?? "";
        MangaReleaseStatus releaseStatus = rawStatus switch
        {
            var s when s.Contains("ongoing") => MangaReleaseStatus.Continuing,
            var s when s.Contains("completed") => MangaReleaseStatus.Completed,
            var s when s.Contains("hiatus") => MangaReleaseStatus.OnHiatus,
            var s when s.Contains("dropped") || s.Contains("cancel") => MangaReleaseStatus.Cancelled,
            _ => MangaReleaseStatus.Continuing
        };

        List<Link> links = [new Link("KatReadingCafe", url)];
        List<AltTitle> altTitles = [];

        Manga manga = new(title, description, coverUrl, releaseStatus, authors, tags, links, altTitles,
            mediaType: MediaType.WebNovel);
        MangaConnectorId<Manga> mangaId = new(manga, this, mangaIdOnSite, url);
        manga.MangaConnectorIds.Add(mangaId);

        return (manga, mangaId);
    }

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId, string? language = null)
    {
        string seriesUrl = mangaId.WebsiteUrl ?? $"https://katreadingcafe.com/series/{mangaId.IdOnConnectorSite}/";

        // Madara themes commonly load the chapter list via an AJAX POST to admin-ajax.php rather than
        // including it in the initial page HTML. Try that first; fall back to parsing the series page
        // directly in case this particular site renders the list server-side.
        string ajaxUrl = seriesUrl.TrimEnd('/') + "/ajax/chapters/";
        HttpResponseMessage response = downloadClient.MakeRequest(ajaxUrl, RequestType.Default, seriesUrl).GetAwaiter().GetResult();
        string html;
        if (response.IsSuccessStatusCode)
        {
            html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        else
        {
            HttpResponseMessage fallback = downloadClient.MakeRequest(seriesUrl, RequestType.Default).GetAwaiter().GetResult();
            if (!fallback.IsSuccessStatusCode)
            {
                Log.Error("Failed to load chapter list (both ajax and direct page)");
                return [];
            }
            html = fallback.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Madara: each chapter is <li class="wp-manga-chapter"><a href="...">Chapter N</a>...</li>
        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//li[contains(@class,'wp-manga-chapter')]//a");
        if (nodes is null)
        {
            Log.Info("No chapters found");
            return [];
        }

        HashSet<string> seenHrefs = new();
        List<(Chapter, MangaConnectorId<Chapter>)> chapters = new();
        foreach (HtmlNode node in nodes)
        {
            string href = node.GetAttributeValue("href", "").Trim();
            if (href.Length == 0 || !seenHrefs.Add(href))
                continue;

            string text = HtmlEntity.DeEntitize(node.InnerText.Trim());
            Match numMatch = Regex.Match(text, @"(?:Chapter|Ch\.?)\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (!numMatch.Success)
                continue;
            string chapterNumber = numMatch.Groups[1].Value;

            string? title = null;
            int dashIdx = text.IndexOf('-');
            if (dashIdx >= 0 && dashIdx + 1 < text.Length)
                title = text[(dashIdx + 1)..].Trim();

            Chapter ch;
            try
            {
                ch = new Chapter(mangaId.Obj, chapterNumber, null, title);
            }
            catch (ArgumentException)
            {
                continue;
            }
            MangaConnectorId<Chapter> mcId = new(ch, this, href, href);
            ch.MangaConnectorIds.Add(mcId);
            chapters.Add((ch, mcId));
        }

        Log.InfoFormat("Found {0} chapters for {1}", chapters.Count, mangaId.Obj.Name);
        return chapters.OrderBy(c => c.Item1, new Chapter.ChapterComparer()).ToArray();
    }

    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId) => [];

    internal override string? GetChapterText(MangaConnectorId<Chapter> chapterId)
    {
        string? url = chapterId.WebsiteUrl;
        if (url is null)
        {
            Log.Error("Chapter URL is null");
            return null;
        }

        HttpResponseMessage response = downloadClient.MakeRequest(url, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to load chapter page");
            return null;
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Madara "novel reading" mode wraps prose in .reading-content, with plain <p> tags inside
        HtmlNode? contentNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'reading-content')]") ??
                                 doc.DocumentNode.SelectSingleNode("//div[contains(@class,'text-left')]");

        if (contentNode is null)
        {
            Log.Error("Could not find chapter content container - site markup may differ from expected Madara layout");
            Log.DebugFormat("Page snippet: {0}", html.Substring(0, Math.Min(1000, html.Length)));
            return null;
        }

        foreach (HtmlNode unwanted in contentNode.SelectNodes(".//script | .//style | .//ins") ?.ToList() ?? [])
            unwanted.Remove();

        StringBuilder sb = new();
        IEnumerable<HtmlNode> paragraphs = contentNode.SelectNodes(".//p") ?? Enumerable.Empty<HtmlNode>();
        foreach (HtmlNode p in paragraphs)
        {
            string text = HtmlEntity.DeEntitize(p.InnerText.Trim());
            if (text.Length == 0)
                continue;
            sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(text)).Append("</p>\n");
        }

        // Some Madara sites put paragraphs as bare text nodes / <br>-separated lines instead of <p> tags.
        if (sb.Length == 0)
        {
            string raw = HtmlEntity.DeEntitize(contentNode.InnerText.Trim());
            foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</p>\n");
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}
