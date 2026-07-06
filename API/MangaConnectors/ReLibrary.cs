using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using API.MangaDownloadClients;
using API.Schema.MangaContext;

namespace API.MangaConnectors;

/// <summary>
/// Connector for re-library.com - a WordPress-based Web Novel translation site.
/// Unlike Manga connectors, this serves text content: GetChapterText is implemented instead of
/// GetChapterImageUrls, and Manga created by this connector are stamped MediaType.WebNovel.
/// </summary>
public class ReLibrary : MangaConnector
{
    public ReLibrary() : base("ReLibrary", ["en"], ["re-library.com"], "https://re-library.com/wp-content/uploads/2023/08/cropped-Cropped-Banner.png", MediaType.WebNovel)
    {
        this.downloadClient = new HttpDownloadClient();
    }

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching: {0}", mangaSearchName);
        // re-library.com's native ?s= search endpoint doesn't actually filter results (it just
        // returns the homepage regardless of query) - so instead we fetch the single directory page
        // that lists every series at once, and filter by title match ourselves before fetching
        // anything else. This also means far fewer requests than checking every listed series.
        string directoryUrl = "https://re-library.com/translations/";
        HttpResponseMessage response = downloadClient.MakeRequest(directoryUrl, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to load series directory page");
            return [];
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Each series entry is a heading (h3/h4/h5, theme-dependent) wrapping a link to /translations/{slug}/
        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes(
            "//h3/a[contains(@href,'/translations/')] | //h4/a[contains(@href,'/translations/')] | //h5/a[contains(@href,'/translations/')]");
        if (nodes is null)
        {
            Log.Info("No series found in directory page - site markup may have changed");
            return [];
        }

        Regex seriesUrlRex = new(@"^https?://re-library\.com/translations/([a-z0-9-]+)/?$");
        HashSet<string> seenSlugs = new();
        List<(string slug, string title)> matches = new();
        foreach (HtmlNode node in nodes)
        {
            string href = node.GetAttributeValue("href", "").Trim();
            Match m = seriesUrlRex.Match(href);
            if (!m.Success || !seenSlugs.Add(m.Groups[1].Value))
                continue;

            string title = HtmlEntity.DeEntitize(node.InnerText.Trim());
            if (title.Contains(mangaSearchName, StringComparison.OrdinalIgnoreCase))
                matches.Add((m.Groups[1].Value, title));
        }

        // Only fetch full details for titles that actually matched - keeps request volume low
        // and avoids getting rate-limited/blocked for unrelated searches.
        List<(Manga, MangaConnectorId<Manga>)> results = new();
        foreach ((string slug, string _) in matches)
        {
            if (GetMangaFromId(slug) is { } manga)
                results.Add(manga);
        }

        Log.InfoFormat("Search '{0}' yielded {1} results.", mangaSearchName, results.Count);
        return results.DistinctBy(r => r.Item1.Key).ToArray();
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        Match m = Regex.Match(url, @"^https?://(?:www\.)?re-library\.com/translations/([a-z0-9-]+)/?");
        if (!m.Success)
            return null;
        return GetMangaFromId(m.Groups[1].Value);
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        string url = $"https://re-library.com/translations/{mangaIdOnSite}/";
        HttpResponseMessage response = downloadClient.MakeRequest(url, RequestType.MangaInfo).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to retrieve series page");
            return null;
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Title: og:title meta, minus the " | Re:Library" suffix
        HtmlNode? titleMeta = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
        string rawTitle = titleMeta?.GetAttributeValue("content", "") ?? mangaIdOnSite;
        string title = Regex.Replace(rawTitle, @"\s*\|\s*Re:Library\s*$", "").Trim();
        title = HtmlEntity.DeEntitize(title);

        // Cover: og:image
        HtmlNode? coverMeta = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
        string coverUrl = coverMeta?.GetAttributeValue("content", "") ?? "";

        // Description: og:description
        HtmlNode? descMeta = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']");
        string description = HtmlEntity.DeEntitize(descMeta?.GetAttributeValue("content", "") ?? "");

        // Tags/genres: links to /tag/{genre}/ within the article metadata area
        HtmlNodeCollection? tagNodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'re-library.com/tag/')]");
        List<MangaTag> tags = tagNodes?
            .Select(n => HtmlEntity.DeEntitize(n.InnerText.Trim()))
            .Where(t => t.Length > 0)
            .Distinct()
            .Select(t => new MangaTag(t))
            .ToList() ?? [];

        // Author: link with rel to an author-like href, best-effort - fallback: no author
        List<Author> authors = [];
        HtmlNode? authorNode = doc.DocumentNode.SelectSingleNode("//a[contains(@href,'/author/')]");
        if (authorNode is not null)
            authors.Add(new Author(HtmlEntity.DeEntitize(authorNode.InnerText.Trim())));

        // Completed status: look for a "Completed" tag among genres, otherwise assume Continuing
        MangaReleaseStatus releaseStatus = tags.Any(t => t.Tag.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            ? MangaReleaseStatus.Completed
            : MangaReleaseStatus.Continuing;

        List<Link> links = [new Link("Re:Library", url)];
        List<AltTitle> altTitles = [];

        Manga manga = new(title, description, coverUrl, releaseStatus, authors, tags, links, altTitles,
            mediaType: MediaType.WebNovel);
        MangaConnectorId<Manga> mangaId = new(manga, this, mangaIdOnSite, url);
        manga.MangaConnectorIds.Add(mangaId);

        return (manga, mangaId);
    }

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId, string? language = null)
    {
        string seriesUrl = mangaId.WebsiteUrl ?? $"https://re-library.com/translations/{mangaId.IdOnConnectorSite}/";
        HttpResponseMessage response = downloadClient.MakeRequest(seriesUrl, RequestType.Default).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to load series page for chapter list");
            return [];
        }

        string html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Chapter links live in <ul><li><a> lists under "Volume N" headings. We don't rely on the heading
        // structure directly (fragile across theme tweaks) - instead we parse volume number and chapter
        // number straight out of each chapter URL, which follows a stable pattern:
        // /translations/{series}/volume-{v}/chapter-{c}[-{n}]-{title-slug}/
        Regex chapterUrlRex = new(@"^https?://re-library\.com/translations/[a-z0-9-]+/volume-(?<vol>\d+)/chapter-(?<chap>\d+(?:-\d+)?)-(?<rest>[a-z0-9-]+)/?$");

        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'/volume-')]");
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
            Match m = chapterUrlRex.Match(href);
            if (!m.Success || !seenHrefs.Add(href))
                continue;

            int volumeNumber = int.Parse(m.Groups["vol"].Value);
            // Chapter number may look like "216-2" for a split part - convert to decimal form (216.2)
            string chapterNumberRaw = m.Groups["chap"].Value.Replace("-", ".");

            string linkText = HtmlEntity.DeEntitize(node.InnerText.Trim());
            // linkText looks like "Chapter 216 - Fairyland Shattered (Part 1)" - strip the leading "Chapter N - " part
            string title = Regex.Replace(linkText, @"^Chapter\s+[\d.]+\s*[-–]?\s*", "").Trim();
            if (title.Length == 0) title = linkText;

            if (!decimal.TryParse(chapterNumberRaw, out _))
                continue; // skip anything we can't parse into the app's chapter-number format

            Chapter ch;
            try
            {
                ch = new Chapter(mangaId.Obj, chapterNumberRaw, volumeNumber, title);
            }
            catch (ArgumentException)
            {
                continue; // chapter number didn't match the app's expected numeric format
            }
            MangaConnectorId<Chapter> mcId = new(ch, this, href, href);
            ch.MangaConnectorIds.Add(mcId);
            chapters.Add((ch, mcId));
        }

        Log.InfoFormat("Found {0} chapters for {1}", chapters.Count, mangaId.Obj.Name);
        return chapters.OrderBy(c => c.Item1, new Chapter.ChapterComparer()).ToArray();
    }

    /// <summary>
    /// Manga (image-based) connectors implement this. ReLibrary is text-based, so this always
    /// returns empty - GetChapterText is used instead by the download worker.
    /// </summary>
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

        // Standard WordPress single-post content container. If the theme uses a different class name,
        // this is the one selector most likely to need adjusting after a real test run.
        HtmlNode? contentNode =
            doc.DocumentNode.SelectSingleNode("//div[contains(@class,'entry-content')]") ??
            doc.DocumentNode.SelectSingleNode("//div[contains(@class,'post-content')]") ??
            doc.DocumentNode.SelectSingleNode("//article//div[contains(@class,'content')]");

        if (contentNode is null)
        {
            Log.Error("Could not find chapter content container - site markup may have changed");
            return null;
        }

        // Strip elements that aren't part of the actual prose: nav links, share buttons, footnote lists,
        // ads, and the support/ko-fi block that appears inline in the content area.
        foreach (HtmlNode unwanted in contentNode.SelectNodes(
                     ".//*[contains(@class,'sharedaddy')] | .//*[contains(@class,'jp-relatedposts')] | " +
                     ".//*[contains(@id,'jp-post-flair')] | .//script | .//style")?.ToList() ?? [])
            unwanted.Remove();

        StringBuilder sb = new();
        IEnumerable<HtmlNode> paragraphs = contentNode.SelectNodes(".//p") ?? Enumerable.Empty<HtmlNode>();
        foreach (HtmlNode p in paragraphs)
        {
            string text = HtmlEntity.DeEntitize(p.InnerText.Trim());
            if (text.Length == 0)
                continue;
            // Skip the repeated Previous/Next/Index nav lines and the "Views: N" footer line
            if (Regex.IsMatch(text, @"^(⇐\s*Previous|Next\s*⇒|⌈\s*Index\s*⌋|Views:\s*\d+)", RegexOptions.IgnoreCase))
                continue;
            sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(text)).Append("</p>\n");
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}
