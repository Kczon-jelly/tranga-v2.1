using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using API.MangaDownloadClients;
using API.Schema.MangaContext;

namespace API.MangaConnectors;

/// <summary>
/// Connector for botitranslation.com - a custom JavaScript single-page app (not a known WP theme),
/// so there's no documented set of CSS class names to target like the other Web Novel connectors.
///
/// Every request goes through ChromiumDownloadClient (headless browser) since the site renders its
/// content client-side via internal API calls that a plain HTTP client would never see.
///
/// Because the exact markup is unknown and unverifiable ahead of time, chapter text extraction uses
/// a generic heuristic instead of specific selectors: after the page fully renders, find the element
/// containing the largest amount of paragraph text (the actual chapter prose is virtually always the
/// biggest block of text on the page - nav bars, ads, and comment sections are comparatively tiny).
/// This is less precise than a hand-tuned selector, but far more likely to keep working if this
/// connector needs it. Expect to revisit this after a first real test.
/// </summary>
public class BotiTranslation : MangaConnector
{
    public BotiTranslation() : base("BotiTranslation", ["en"], ["botitranslation.com", "www.botitranslation.com"],
        "https://www.botitranslation.com/favicon.ico", MediaType.WebNovel)
    {
        this.downloadClient = new ChromiumDownloadClient();
    }

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching: {0}", mangaSearchName);
        string requestUrl = $"https://www.botitranslation.com/explore?search={HttpUtility.UrlEncode(mangaSearchName)}";
        HttpResponseMessage response = downloadClient.MakeRequest(requestUrl, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Search request failed");
            return [];
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Book links follow the pattern /book/{id}-{slug}
        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'/book/')]");
        if (nodes is null)
        {
            Log.Info("No results found");
            return [];
        }

        Regex bookUrlRex = new(@"/book/(?<id>\d+-[a-z0-9-]+)");
        HashSet<string> seenIds = new();
        List<(Manga, MangaConnectorId<Manga>)> results = new();
        foreach (HtmlNode node in nodes)
        {
            string href = node.GetAttributeValue("href", "").Trim();
            Match m = bookUrlRex.Match(href);
            if (!m.Success || !seenIds.Add(m.Groups["id"].Value))
                continue;

            if (GetMangaFromId(m.Groups["id"].Value) is { } manga)
                results.Add(manga);
        }

        Log.InfoFormat("Search '{0}' yielded {1} results.", mangaSearchName, results.Count);
        return results.DistinctBy(r => r.Item1.Key).ToArray();
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        Match m = Regex.Match(url, @"botitranslation\.com/book/(?<id>\d+-[a-z0-9-]+)");
        return m.Success ? GetMangaFromId(m.Groups["id"].Value) : null;
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        string url = $"https://www.botitranslation.com/book/{mangaIdOnSite}";
        HttpResponseMessage response = downloadClient.MakeRequest(url, RequestType.MangaInfo).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to retrieve book page");
            return null;
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Title: rendered <title> tag or first <h1> on the page
        string title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? mangaIdOnSite;
        title = Regex.Replace(title, @"\s*[|\-–]\s*(Boti\s*Translation|BOTI\s*Translation).*$", "", RegexOptions.IgnoreCase).Trim();
        if (title.Length == 0)
        {
            HtmlNode? h1 = doc.DocumentNode.SelectSingleNode("//h1");
            title = h1 is not null ? HtmlEntity.DeEntitize(h1.InnerText.Trim()) : mangaIdOnSite;
        }

        // Cover: first meaningfully-sized <img>, or og:image if present
        HtmlNode? ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
        string coverUrl = ogImage?.GetAttributeValue("content", "") ?? "";
        if (string.IsNullOrEmpty(coverUrl))
        {
            HtmlNode? img = doc.DocumentNode.SelectSingleNode("//img[contains(@src,'/cover') or contains(@class,'cover')]") ??
                             doc.DocumentNode.SelectSingleNode("//img");
            coverUrl = img?.GetAttributeValue("src", "") ?? "";
        }

        // Description: og:description, best-effort
        HtmlNode? ogDesc = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']");
        string description = HtmlEntity.DeEntitize(ogDesc?.GetAttributeValue("content", "") ?? "");

        List<Link> links = [new Link("BotiTranslation", url)];

        Manga manga = new(HtmlEntity.DeEntitize(title), description, coverUrl, MangaReleaseStatus.Continuing,
            [], [], links, [], mediaType: MediaType.WebNovel);
        MangaConnectorId<Manga> mangaId = new(manga, this, mangaIdOnSite, url);
        manga.MangaConnectorIds.Add(mangaId);

        return (manga, mangaId);
    }

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId, string? language = null)
    {
        string bookUrl = mangaId.WebsiteUrl ?? $"https://www.botitranslation.com/book/{mangaId.IdOnConnectorSite}";
        HttpResponseMessage response = downloadClient.MakeRequest(bookUrl, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to load book page for chapter list");
            return [];
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Chapter links follow /chapter/{id}[-{slug}]
        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'/chapter/')]");
        if (nodes is null)
        {
            Log.Info("No chapters found - the chapter list may load via lazy-scroll/pagination this connector doesn't trigger yet");
            return [];
        }

        Regex chapterUrlRex = new(@"/chapter/(?<id>\d+)(?:-[a-z0-9-]+)?");
        HashSet<string> seenIds = new();
        List<(Chapter, MangaConnectorId<Chapter>)> chapters = new();
        int sequentialNumber = 0;
        foreach (HtmlNode node in nodes)
        {
            string href = node.GetAttributeValue("href", "").Trim();
            Match m = chapterUrlRex.Match(href);
            if (!m.Success || !seenIds.Add(m.Groups["id"].Value))
                continue;

            string fullUrl = href.StartsWith("http") ? href : $"https://www.botitranslation.com{href}";
            string text = HtmlEntity.DeEntitize(node.InnerText.Trim());
            Match numMatch = Regex.Match(text, @"Chapter\s*([\d.]+)", RegexOptions.IgnoreCase);
            string chapterNumber = numMatch.Success ? numMatch.Groups[1].Value : (++sequentialNumber).ToString();

            string? title = null;
            int dashIdx = text.IndexOf(':');
            if (dashIdx < 0) dashIdx = text.IndexOf('-');
            if (dashIdx >= 0 && dashIdx + 1 < text.Length)
                title = text[(dashIdx + 1)..].Trim();

            Chapter ch;
            try
            {
                ch = new Chapter(mangaId.Obj, chapterNumber, null, title is { Length: > 0 } ? title : null);
            }
            catch (ArgumentException)
            {
                continue;
            }
            MangaConnectorId<Chapter> mcId = new(ch, this, m.Groups["id"].Value, fullUrl);
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

        foreach (HtmlNode unwanted in doc.DocumentNode.SelectNodes("//script | //style | //nav | //header | //footer")?.ToList() ?? [])
            unwanted.Remove();

        // Heuristic: find the container element whose direct <p> children hold the most total text.
        // This avoids depending on any specific class name, which we have no way to verify for this site.
        HtmlNodeCollection? candidates = doc.DocumentNode.SelectNodes("//div | //article | //main | //section");
        HtmlNode? bestContainer = null;
        int bestLength = 0;
        if (candidates is not null)
        {
            foreach (HtmlNode candidate in candidates)
            {
                IEnumerable<HtmlNode> directParagraphs = candidate.ChildNodes.Where(c => c.Name == "p");
                int totalLength = directParagraphs.Sum(p => p.InnerText.Trim().Length);
                if (totalLength > bestLength)
                {
                    bestLength = totalLength;
                    bestContainer = candidate;
                }
            }
        }

        if (bestContainer is null || bestLength < 200)
        {
            Log.Error("Could not confidently locate chapter text on the rendered page - this connector's heuristic may need adjusting for this site's actual layout");
            return null;
        }

        StringBuilder sb = new();
        foreach (HtmlNode p in bestContainer.ChildNodes.Where(c => c.Name == "p"))
        {
            string text = HtmlEntity.DeEntitize(p.InnerText.Trim());
            if (text.Length == 0)
                continue;
            sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(text)).Append("</p>\n");
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}
