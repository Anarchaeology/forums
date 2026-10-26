// BuildForum.cs
// Builds the forum archive site from ExtractForum.cs's threads.jsonl.gz, in the look of the Anarchy Online
// Bulletin Board as it was in 2002 (vBulletin 2 with Funcom's "anarchytheme"). Writes, in SITEFOLDER:
//   index.html             the board list, in the 2002 categories, with each board's 2002 description
//   forum/<board>.html     thread lists, latest activity first, 50 threads a page (<board>-2.html, ...)
//   thread/<key>.html      threads, 20 posts a page (<key>-2.html, ...); launch-forum threads are l<id>
//   about.html, style.css, robots.txt, images/ (the 2002 theme images, from THEMEFOLDER)
// Pages left from an earlier build that this build no longer makes are removed (in forum/ and thread/ only).
//
// Needs the .NET 10 SDK. Run from this tools folder:
//   dotnet run BuildForum.cs -- THREADS.jsonl.gz THEMEFOLDER SITEFOLDER [--repo URL]
// THEMEFOLDER holds images/... as saved from the 2002 forum.

#:property PublishAot=false
#:property NoWarn=IL2026;IL3050

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length < 3) { Console.Error.WriteLine("Usage: dotnet run BuildForum.cs -- THREADS.jsonl.gz THEMEFOLDER SITEFOLDER [--repo URL]"); return 2; }
string threadsPath = args[0], themeDir = args[1], siteDir = args[2];
string repo = args.SkipWhile(a => a != "--repo").Skip(1).FirstOrDefault() ?? "";
const int PostsPerPage = 20, ThreadsPerPage = 50;
var en = CultureInfo.InvariantCulture;

// ---- Boards, in the 2002 order ----
var boards = new List<Board>();
foreach (var f in File.ReadLines("data/boards.tsv").Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')))
    boards.Add(new Board(f[0], f[1], f.Length > 2 ? f[2] : "", f.Length > 3 && f[3] != "" ? int.Parse(f[3]) : 0, f.Length > 5 ? f[5] : ""));
var boardByName = boards.ToDictionary(b => b.Category + "\t" + b.Name);

// ---- Members are their forum accounts. Posts that don't show one (the 2001 launch forum had its own accounts, and
// some pages saved from the live forum leave the account out) go to the main forum's account with the same name; a
// name used by two accounts goes to the one with more posts. ----
var accountByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
{
    var postsByAccount = new Dictionary<(string Name, string Uid), int>();
    foreach (var line in ReadLines(threadsPath))
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        if (r.GetProperty("src").GetString() == "launch") continue;
        foreach (var p in r.GetProperty("posts").EnumerateArray())
            if (Str(p, "uid") is var uid && uid != "")
            {
                var k = (Str(p, "a").ToLowerInvariant(), uid);
                postsByAccount[k] = postsByAccount.GetValueOrDefault(k) + 1;
            }
    }
    foreach (var g in postsByAccount.GroupBy(kv => kv.Key.Name))
        accountByName[g.Key] = g.MaxBy(kv => kv.Value).Key.Uid;
}

// ---- Pass 1: a summary of every thread, and which page each post lands on ----
var threads = new List<ThreadInfo>();
var postPage = new Dictionary<long, (string Key, int Page)>();
var lastAvatar = new Dictionary<string, (int Rank, DateTime When, string Avatar)>();
var joined = new Dictionary<string, (DateTime Saved, DateTime First)>();
var memberPosts = new Dictionary<string, int>();
foreach (var line in ReadLines(threadsPath))
{
    using var doc = JsonDocument.Parse(line);
    var r = doc.RootElement;
    var posts = r.GetProperty("posts");
    int n = posts.GetArrayLength();
    var t = new ThreadInfo
    {
        Key = r.GetProperty("key").GetString()!, Src = r.GetProperty("src").GetString()!, Title = r.GetProperty("title").GetString()!,
        Board = boardByName[r.GetProperty("cat").GetString() + "\t" + r.GetProperty("board").GetString()],
        Posts = n, Pages = Math.Max(1, (n + PostsPerPage - 1) / PostsPerPage),
    };
    if (t.Title == "") t.Title = "(no title)";
    int i = 0;
    foreach (var p in posts.EnumerateArray())
    {
        var d = DateTime.Parse(p.GetProperty("d").GetString()!, en);
        string author = p.GetProperty("a").GetString()!;
        if (i == 0) { t.Starter = author; t.First = d; t.Icon = p.TryGetProperty("ic", out var ic) ? ic.GetInt32() : 0; }
        if (d >= t.Last) { t.Last = d; t.LastAuthor = author; }
        if (!t.Key.StartsWith('l') && p.TryGetProperty("pid", out var pid)) postPage[pid.GetInt64()] = (t.Key, i / PostsPerPage + 1);
        // A member's avatar is theirs, not the post's: the last one recorded goes on all their posts. Funcom's
        // archive (saved 2025) shows each member's final avatar; otherwise the latest live-forum page decides.
        int rank = t.Src == "archive" ? 2 : 1;
        string member = MemberKey(t.Src, Str(p, "uid"), author);
        if (!lastAvatar.TryGetValue(member, out var known) || rank > known.Rank || (rank == known.Rank && d > known.When))
            lastAvatar[member] = (rank, d, Str(p, "av"));
        // A member's join month: the forum's own "Registered" month where a saved page shows one, otherwise the
        // month of their first post here. The forum showed "Jan 1970" for accounts with no join date; those count
        // as unknown.
        string reg = Str(p, "reg");
        var saved = reg != "" ? DateTime.ParseExact(reg, "MMM yyyy", en) : DateTime.MaxValue;
        if (saved.Year < 2000) saved = DateTime.MaxValue;
        joined[member] = joined.TryGetValue(member, out var j)
            ? (saved < j.Saved ? saved : j.Saved, d < j.First ? d : j.First) : (saved, d);
        // Post counts are counted here: the saved pages' own counts are from whatever day each page was saved.
        memberPosts[member] = memberPosts.GetValueOrDefault(member) + 1;
        i++;
    }
    threads.Add(t);
    t.Board.Threads.Add(t);
    t.Board.PostCount += n;
}
foreach (var b in boards) b.Threads.Sort((x, y) => y.Last.CompareTo(x.Last));
Console.WriteLine($"{threads.Count} threads, {threads.Sum(t => t.Posts)} posts");

// ---- Output ----
Directory.CreateDirectory(siteDir);
var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
void Write(string rel, string html)
{
    string path = Path.GetFullPath(Path.Combine(siteDir, rel));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, html, new UTF8Encoding(false));
    written.Add(path);
}

// Theme images and site files
foreach (var file in Directory.EnumerateFiles(Path.Combine(themeDir, "images"), "*", SearchOption.AllDirectories))
{
    string dest = Path.Combine(siteDir, Path.GetRelativePath(themeDir, file));
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(file).Length) File.Copy(file, dest, true);
}
File.WriteAllText(Path.Combine(siteDir, "style.css"), Css, new UTF8Encoding(false));
File.WriteAllText(Path.Combine(siteDir, "robots.txt"), Robots, new UTF8Encoding(false));
File.WriteAllText(Path.Combine(siteDir, ".nojekyll"), "");

// The board list
{
    var sb = new StringBuilder();
    sb.Append(Header("", "Anarchy Online Bulletin Board", "", ""));
    int totalThreads = threads.Count, totalPosts = threads.Sum(t => t.Posts);
    sb.Append($"""
        <table class="welcome"><tr><td colspan="2">
        <b>Welcome to the Anarchy Online Bulletin Board archive.</b><br><br>
        This is Funcom's official Anarchy Online forum as it was from the beta in May 2001 to the end of 2003,
        rebuilt from the copies the Internet Archive's Wayback Machine saved. Nothing can be posted any more.
        Read <a href="about.html">about this archive</a> for where the threads come from and what didn't survive.<hr>
        </td></tr><tr><td class="small">Total Threads: <b>{totalThreads:N0}</b> | Total Posts: <b>{totalPosts:N0}</b></td>
        <td class="small right">All times are GMT.</td></tr></table>
        <br>
        <table class="tborder"><tr class="thead"><td>&nbsp;</td><td class="left wide">Forum</td><td class="num">Posts</td><td class="num">Threads</td><td>Last Post</td></tr>

        """);
    foreach (var cat in boards.GroupBy(b => b.Category))
    {
        sb.Append($"<tr class=\"cat\"><td colspan=\"5\"><b>{Enc(cat.Key)}</b></td></tr>\n");
        foreach (var b in cat)
        {
            var last = b.Threads.FirstOrDefault();
            sb.Append($"<tr><td class=\"alt2 icon\"><img src=\"images/{(b.Threads.Count > 0 ? "off" : "lock")}.gif\" alt=\"\"></td>")
              .Append($"<td class=\"alt1 left\">{(b.Threads.Count > 0 ? $"<a href=\"forum/{b.Slug}.html\"><b>{Enc(b.Shown)}</b></a>" : $"<b>{Enc(b.Shown)}</b>")}")
              .Append(b.Description != "" ? $"<br><span class=\"small\">{Enc(b.Description)}</span>" : "")
              .Append($"</td><td class=\"alt2 num\">{b.PostCount:N0}</td><td class=\"alt1 num\">{b.Threads.Count:N0}</td><td class=\"alt2 lastpost\">")
              .Append(last is null ? "<span class=\"small\">-</span>" : LastPostCell(last, "thread/"))
              .Append("</td></tr>\n");
        }
    }
    sb.Append("</table>\n");
    sb.Append(Footer(""));
    Write("index.html", sb.ToString());
}

// Thread lists
foreach (var b in boards.Where(b => b.Threads.Count > 0))
{
    int pages = (b.Threads.Count + ThreadsPerPage - 1) / ThreadsPerPage;
    for (int page = 1; page <= pages; page++)
    {
        var sb = new StringBuilder();
        string nav = $"<a href=\"../index.html\">Anarchy Online Bulletin Board</a> &gt; {Enc(b.Category)} &gt; {Enc(b.Shown)}";
        sb.Append(Header("../", $"Anarchy Online Bulletin Board - {b.Shown}", nav, ""));
        if (b.Description != "") sb.Append($"<p class=\"small boarddesc\">{Enc(b.Description)}</p>\n");
        string pageNav = PageNav(pages, page, p => $"{b.Slug}{(p > 1 ? "-" + p : "")}.html");
        sb.Append($"<div class=\"pagenav\">{pageNav}</div>\n");
        sb.Append("<table class=\"tborder threads\"><tr class=\"thead\"><td colspan=\"3\">Thread</td><td class=\"starter\">Thread Starter</td><td class=\"num\">Replies</td><td>Last Post</td></tr>\n");
        foreach (var t in b.Threads.Skip((page - 1) * ThreadsPerPage).Take(ThreadsPerPage))
        {
            sb.Append($"<tr><td class=\"alt1 icon\"><img src=\"../images/{(t.Posts > 15 ? "hotfolder" : "folder")}.gif\" alt=\"\"></td>")
              .Append($"<td class=\"alt2 icon\">{(t.Icon > 0 ? $"<img src=\"../images/icons/icon{t.Icon}.gif\" alt=\"\">" : "&nbsp;")}</td>")
              .Append($"<td class=\"alt1 left title\"><a href=\"../thread/{t.Key}.html\">{Enc(t.Title)}</a>");
            if (t.Pages > 1)
            {
                sb.Append(" <span class=\"small\">( <img src=\"../images/multipage.gif\" alt=\"\"> ");
                IEnumerable<int> nums = t.Pages <= 5 ? Enumerable.Range(1, t.Pages) : new[] { 1, 2, 3, t.Pages };
                int prev = 0;
                foreach (int n in nums) { if (n > prev + 1) sb.Append("... "); sb.Append($"<a href=\"../thread/{ThreadFile(t.Key, n)}\">{n}</a> "); prev = n; }
                sb.Append(")</span>");
            }
            sb.Append($"<br class=\"mobile\"><span class=\"mobile small\">{Enc(t.Starter)}, {t.Posts - 1:N0} {(t.Posts == 2 ? "reply" : "replies")}</span></td>")
              .Append($"<td class=\"alt2 starter\">{Enc(t.Starter)}</td><td class=\"alt1 num\">{t.Posts - 1:N0}</td>")
              .Append($"<td class=\"alt2 lastpost\">{LastPostCell(t, "../thread/")}</td></tr>\n");
        }
        sb.Append("</table>\n");
        sb.Append($"<div class=\"pagenav\">{pageNav}</div>\n");
        sb.Append(Footer("../"));
        Write($"forum/{b.Slug}{(page > 1 ? "-" + page : "")}.html", sb.ToString());
    }
}

// Threads (pass 2)
var threadByKey = threads.ToDictionary(t => t.Key);
var linkRx = new Regex(@"(href|src)=""@(t:([^""]+)|p:(\d+)|img/)", RegexOptions.Compiled);
var goneRx = new Regex(@"<a href=""@gone[^""]*"">([\s\S]*?)</a>", RegexOptions.Compiled);
int threadPages = 0;
foreach (var line in ReadLines(threadsPath))
{
    using var doc = JsonDocument.Parse(line);
    var r = doc.RootElement;
    var t = threadByKey[r.GetProperty("key").GetString()!];
    var posts = r.GetProperty("posts").EnumerateArray().ToList();
    int later = r.GetProperty("later").GetInt32(), gapAfter = r.GetProperty("gapAfter").GetInt32();
    string saved = r.TryGetProperty("saved", out var sv) ? sv.GetString() ?? "" : "";
    int index = t.Board.Threads.IndexOf(t);
    var newer = index > 0 ? t.Board.Threads[index - 1] : null;
    var older = index + 1 < t.Board.Threads.Count ? t.Board.Threads[index + 1] : null;
    for (int page = 1; page <= t.Pages; page++)
    {
        var sb = new StringBuilder();
        int boardPage = index / ThreadsPerPage + 1;
        string nav = $"<a href=\"../index.html\">Anarchy Online Bulletin Board</a> &gt; {Enc(t.Board.Category)} &gt; " +
                     $"<a href=\"../forum/{t.Board.Slug}{(boardPage > 1 ? "-" + boardPage : "")}.html\">{Enc(t.Board.Shown)}</a> &gt; {Enc(t.Title)}";
        sb.Append(Header("../", $"Anarchy Online Bulletin Board - {t.Title}", nav, t.Src == "launch" ? "From the 2001 launch forum" : ""));
        string pageNav = PageNav(t.Pages, page, p => ThreadFile(t.Key, p));
        sb.Append("<table class=\"threadnav\"><tr><td>").Append(t.Pages > 1 ? pageNav : "&nbsp;").Append("</td><td class=\"right small\">")
          .Append(older is null ? "" : $"<img src=\"../images/prev.gif\" alt=\"\"> <a href=\"{older.Key}.html\">Last Thread</a> &nbsp; ")
          .Append(newer is null ? "" : $"<a href=\"{newer.Key}.html\">Next Thread</a> <img src=\"../images/next.gif\" alt=\"\">")
          .Append("</td></tr></table>\n");
        // Only thread pages go into the search index (data-pagefind-body), titled by the thread, filterable by board and year.
        sb.Append($"<table class=\"tborder posts\" data-pagefind-body data-pagefind-meta=\"title:{Enc(t.Title)}{(page > 1 ? $" (page {page})" : "")}\">")
          .Append($"<tr class=\"thead\" data-pagefind-ignore><td class=\"author\">Author<span data-pagefind-filter=\"board:{Enc(t.Board.Shown)}\"></span>")
          .Append($"<span data-pagefind-filter=\"year:{t.First.Year}\"></span></td><td>Thread</td></tr>\n");
        int start = (page - 1) * PostsPerPage;
        for (int i = start; i < Math.Min(posts.Count, start + PostsPerPage); i++)
        {
            var p = posts[i];
            if (p.TryGetProperty("gap", out var gap))
                sb.Append($"<tr><td colspan=\"2\" class=\"gap\">{GapText(gap.GetInt32())}</td></tr>\n");
            string alt = i % 2 == 0 ? "alt2" : "alt1";
            string author = Str(p, "a"), userTitle = Str(p, "ut"), reg = Str(p, "reg");
            string member = MemberKey(t.Src, Str(p, "uid"), author);
            string pc = memberPosts[member].ToString(en);
            string avatar = lastAvatar.TryGetValue(member, out var la) ? la.Avatar : Str(p, "av");
            // One join month per member on all their posts; a saved month later than their first post here (accounts
            // re-made after 2003, seen on pages saved later) gives way to the first post's month.
            if (joined.TryGetValue(member, out var jn))
            {
                var firstMonth = new DateTime(jn.First.Year, jn.First.Month, 1);
                reg = (jn.Saved <= firstMonth ? jn.Saved : firstMonth).ToString("MMM yyyy", en);
            }
            if (avatar != "" && !File.Exists(Path.Combine(themeDir, "images", "avatars", avatar))) avatar = "";
            string anchor = p.TryGetProperty("pid", out var pid) ? $" id=\"post{pid.GetInt64()}\"" : "";
            var when = DateTime.Parse(Str(p, "d"), en);
            sb.Append($"<tr class=\"post\"{anchor}><td class=\"{alt} author\"><b class=\"name\">{Enc(author)}</b>")
              .Append(userTitle != "" ? $"<br><span class=\"small usertitle\">{Enc(userTitle)}</span>" : "")
              .Append(avatar != "" ? $"<br><img class=\"avatar\" src=\"../images/avatars/{avatar}\" alt=\"\">" : "")
              .Append(reg != "" || pc != "" ? $"<p class=\"small userinfo\" data-pagefind-ignore>{(reg != "" ? $"Registered: {Enc(reg)}<br>" : "")}{(pc != "" ? $"Posts: {Enc(pc)}" : "")}</p>" : "")
              .Append($"</td><td class=\"{alt} body\">");
            string title = Str(p, "t");
            int icon = p.TryGetProperty("ic", out var ic) ? ic.GetInt32() : 0;
            if (title != "" || icon > 0)
                sb.Append("<div class=\"small posttitle\">").Append(icon > 0 ? $"<img src=\"../images/icons/icon{icon}.gif\" alt=\"\"> " : "").Append(title != "" ? $"<b>{Enc(title)}</b>" : "").Append("</div>");
            string body = Resolve(Str(p, "h"));
            sb.Append("<div class=\"message\">").Append(body != "" ? body : "<i class=\"small\">(This post is empty in the saved copy.)</i>").Append("</div>");
            string sig = Str(p, "sig");
            if (sig != "") sb.Append("<div class=\"sig\">__________________<br>").Append(Resolve(sig)).Append("</div>");
            string edit = Str(p, "edit");
            if (edit != "" && edit.Split('|') is [var by, var at])
                sb.Append($"<p class=\"small edited\"><i>Last edited by {Enc(by)} on {LongDate(DateTime.Parse(at, en))} at {DateTime.Parse(at, en):HH:mm}</i></p>");
            sb.Append($"</td></tr>\n<tr class=\"postfoot\" data-pagefind-ignore><td class=\"{alt} small date\"><img src=\"../images/posticon.gif\" alt=\"\"> {LongDate(when)} <span class=\"time\">{when:HH:mm}</span></td>")
              .Append($"<td class=\"{alt} small right\">{(anchor != "" ? $"<a href=\"#post{pid.GetInt64()}\">#{i + 1}</a>" : $"#{i + 1}")}</td></tr>\n");
        }
        if (page == t.Pages)
        {
            if (gapAfter > 0) sb.Append($"<tr><td colspan=\"2\" class=\"gap\">{(gapAfter == 1 ? "The last page of this thread wasn't saved." : $"The last {gapAfter} pages of this thread weren't saved.")}</td></tr>\n");
            if (later > 0) sb.Append($"<tr><td colspan=\"2\" class=\"gap\">This thread continued after 2003; its {later:N0} later {(later == 1 ? "reply isn't" : "replies aren't")} part of this archive.</td></tr>\n");
        }
        sb.Append("</table>\n");
        sb.Append($"<table class=\"threadnav\"><tr><td>{(t.Pages > 1 ? pageNav : "&nbsp;")}</td><td class=\"right small\">")
          .Append(saved != "" ? $"<a href=\"{Enc(saved)}\">The saved copy</a> ({SourceName(t.Src)})" : SourceName(t.Src))
          .Append("</td></tr></table>\n");
        sb.Append(Footer("../"));
        Write($"thread/{ThreadFile(t.Key, page)}", sb.ToString());
        threadPages++;
    }
}
Console.WriteLine($"{threadPages} thread pages, {boards.Sum(b => (b.Threads.Count + ThreadsPerPage - 1) / ThreadsPerPage)} thread-list pages");

// About this archive, and the search page (its index, pagefind/, is made after the build by Pagefind)
Write("about.html", About());
// The random button: a page that sends the reader to a random thread. The thread list is in the page itself, so
// it costs nothing until someone clicks.
Write("random.html", Header("", "Anarchy Online Bulletin Board - Random thread",
    "<a href=\"index.html\">Anarchy Online Bulletin Board</a> &gt; Random thread", "") + $"""
    <p id="random-note">Picking a random thread...</p>
    <script>
    const keys = "{string.Join(' ', threads.Select(t => t.Key))}".split(" ");
    location.replace("thread/" + keys[Math.floor(Math.random() * keys.length)] + ".html");
    </script>
    <noscript><p>The random thread needs JavaScript. <a href="index.html">Back to the board list</a></p></noscript>

    """ + Footer(""));
Write("search.html", Header("", "Anarchy Online Bulletin Board - Search",
    "<a href=\"index.html\">Anarchy Online Bulletin Board</a> &gt; Search", "") + """
    <link href="pagefind/pagefind-ui.css" rel="stylesheet">
    <div id="search"></div>
    <script src="pagefind/pagefind-ui.js"></script>
    <script>
    // The index records pages as /thread/...; baseUrl puts them under wherever the site lives (/forums/ on GitHub Pages).
    if (window.PagefindUI)
      new PagefindUI({ element: "#search", showSubResults: true, showImages: false, resetStyles: false, pageSize: 10,
        baseUrl: location.pathname.replace(/search\.html.*$/, ""),
        translations: { placeholder: "Search posts, titles and names" } });
    else
      document.getElementById("search").innerHTML = "<p>Search isn't available yet: its index hasn't been built.</p>";
    </script>

    """ + Footer(""));

// Remove pages from earlier builds that weren't written this time
int removed = 0;
foreach (string dir in new[] { "forum", "thread" })
{
    string full = Path.Combine(siteDir, dir);
    if (!Directory.Exists(full)) continue;
    foreach (var file in Directory.EnumerateFiles(full, "*.html"))
        if (!written.Contains(Path.GetFullPath(file))) { File.Delete(file); removed++; }
}
if (removed > 0) Console.WriteLine($"removed {removed} pages from an earlier build");
long bytes = Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)).Sum(f => new FileInfo(f).Length);
Console.WriteLine($"site: {bytes / 1048576.0:N0} MB");
return 0;

// ---- Helpers ----
// Links to threads and posts we have point at our pages; links to ones left out (the off-topic board, posts after
// 2003) become plain text.
string Resolve(string html) => goneRx.Replace(linkRx.Replace(html, m =>
{
    if (m.Groups[2].Value == "img/") return $"{m.Groups[1].Value}=\"../images/";
    if (m.Groups[3].Success)
        return threadByKey.ContainsKey(m.Groups[3].Value) ? $"{m.Groups[1].Value}=\"{m.Groups[3].Value}.html" : $"{m.Groups[1].Value}=\"@gone";
    var pid = long.Parse(m.Groups[4].Value);
    return postPage.TryGetValue(pid, out var at) ? $"{m.Groups[1].Value}=\"{ThreadFile(at.Key, at.Page)}#post{pid}" : $"{m.Groups[1].Value}=\"@gone";
}), "$1");

string LastPostCell(ThreadInfo t, string threadDir) =>
    $"<span class=\"small\">{LongDate(t.Last)} <span class=\"time\">{t.Last:HH:mm}</span><br>by <b>{Enc(t.LastAuthor)}</b></span> " +
    $"<a href=\"{threadDir}{ThreadFile(t.Key, t.Pages)}\"><img src=\"{(threadDir.StartsWith("..") ? "../" : "")}images/lastpost.gif\" alt=\"Go to last post\"></a>";

// Used only on the About page; the boards themselves carry no notes, so they read as they did in 2002.
string CoverageNote(Board b)
{
    if (b.Aug2002 == 0) return "";
    int saved = b.Threads.Count(t => t.First < new DateTime(2002, 8, 19));
    if (saved >= b.Aug2002 / 2) return "";
    return b.Threads.Count == 0
        ? $"This board had {b.Aug2002:N0} threads in August 2002. None of them were saved."
        : $"This board had {b.Aug2002:N0} threads in August 2002; only {saved:N0} from then were saved.";
}

static string GapText(int pages) => pages == 1 ? "A page of this thread (up to 20 posts) wasn't saved." : $"{pages} pages of this thread weren't saved.";

static string SourceName(string src) => src switch
{
    "archive" => "Funcom's forum archive, saved by the Wayback Machine in 2025",
    "live" => "saved by the Wayback Machine while the forum was live",
    _ => "the 2001 launch forum, saved by the Wayback Machine",
};

// Members by forum number (the archive and the live forum share them); by name when a page didn't say. The launch
// forum had its own accounts, and its printable pages give names only, so its members go by name.
string MemberKey(string src, string uid, string author) =>
    src != "launch" && uid != "" ? uid : accountByName.TryGetValue(author, out var account) ? account : "name:" + author.ToLowerInvariant();

static string ThreadFile(string key, int page) => page <= 1 ? $"{key}.html" : $"{key}-{page}.html";

static string PageNav(int pages, int current, Func<int, string> href)
{
    if (pages <= 1) return "";
    var sb = new StringBuilder($"<span class=\"small\">Pages ({pages}): ");
    if (current > 3) sb.Append($"<a href=\"{href(1)}\">&laquo; First</a> ... ");
    if (current > 1) sb.Append($"<a href=\"{href(current - 1)}\">&laquo;</a> ");
    for (int p = Math.Max(1, current - 2); p <= Math.Min(pages, current + 2); p++)
        sb.Append(p == current ? $"<b>[{p}]</b> " : $"<a href=\"{href(p)}\">{p}</a> ");
    if (current < pages) sb.Append($"<a href=\"{href(current + 1)}\">&raquo;</a> ");
    if (current < pages - 2) sb.Append($"... <a href=\"{href(pages)}\">Last &raquo;</a>");
    return sb.Append("</span>").ToString();
}

static string LongDate(DateTime d)
{
    int day = d.Day;
    string suffix = day is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
    return $"{d.ToString("MMMM", CultureInfo.InvariantCulture)} {day}{suffix}, {d.Year}";
}

static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
static string Enc(string s) => WebUtility.HtmlEncode(s);

static IEnumerable<string> ReadLines(string path)
{
    using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), Encoding.UTF8);
    while (reader.ReadLine() is { } line) if (line.Length > 0) yield return line;
}

static string Header(string root, string title, string nav, string badge) => $"""
    <!doctype html>
    <html lang="en">
    <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{WebUtility.HtmlEncode(title)}</title>
    <link rel="stylesheet" href="{root}style.css">
    <link rel="icon" href="{root}images/vb_bullet.gif">
    </head>
    <body>
    <div class="page">
    <table class="top"><tr>
    <td class="logo"><a href="{root}index.html"><img src="{root}images/anarchytheme/logos/aologo.jpg" alt="Anarchy Online Bulletin Board"></a></td>
    <td class="toplinks"><a href="{root}about.html"><img src="{root}images/top_faq.gif" alt="About this archive" title="About this archive"></a> <a href="{root}search.html"><img src="{root}images/top_search.gif" alt="Search" title="Search"></a> <a href="{root}index.html"><img src="{root}images/top_home.gif" alt="Home" title="Home"></a> <a href="{root}random.html"><img src="{root}images/top_random.png" alt="Random thread" title="Random thread"></a></td>
    </tr></table>
    <div class="content">
    {(nav != "" ? $"<div class=\"navbar\"><img src=\"{root}images/vb_bullet.gif\" alt=\"\"> <b>{nav}</b>{(badge != "" ? $" <span class=\"small badge\">{badge}</span>" : "")}</div>\n" : "")}
    """;

static string Footer(string root) => $"""
    <p class="small tz">All times are GMT.</p>
    </div>
    <p class="footer"><b>&lt; <a href="{root}about.html">About this archive</a> - <a href="https://anarchaeology.github.io/">Anarchaeology</a> - <a href="https://web.archive.org/">Internet Archive</a> &gt;</b></p>
    <p class="footer small">The Anarchy Online forums of 2001-2003, rebuilt from copies saved by the Internet Archive's Wayback Machine.<br>
    Not affiliated with Funcom. Anarchy Online is a trademark of Funcom. The posts are their writers' own.</p>
    </div>
    </body>
    </html>

    """;

string About()
{
    int archive = threads.Count(t => t.Src == "archive"), live = threads.Count(t => t.Src == "live"), launch = threads.Count(t => t.Src == "launch");
    string issues = repo != "" ? $"<a href=\"{Enc(repo)}/issues\">open an issue</a>" : "get in touch";
    var missing = boards.Where(b => b.Aug2002 > 0 && CoverageNote(b) != "").Select(b => $"{Enc(b.Name)} ({b.Aug2002:N0})");
    return Header("", "Anarchy Online Bulletin Board - About this archive",
        "<a href=\"index.html\">Anarchy Online Bulletin Board</a> &gt; About this archive", "") + $"""
        <div class="about">
        <h2>About this archive</h2>
        <p>Funcom ran the official Anarchy Online forums from before the game's launch in June 2001. Years later the old
        forum became a read-only archive at forums-archive.anarchy-online.com, which went offline in 2025. This site
        rebuilds the forum's first years, from the beta in May 2001 to the end of 2003, from the copies the Internet
        Archive's <a href="https://web.archive.org/">Wayback Machine</a> saved, and shows them the way the forum
        looked in 2002.</p>

        <h3>Where the threads come from</h3>
        <ul>
        <li><b>Funcom's forum archive</b>, saved by the Wayback Machine in 2025: {archive:N0} threads.</li>
        <li><b>The live forum</b>: {live:N0} threads the Wayback Machine saved from the forum itself, mostly while it
        was in use and some years later, that Funcom's archive didn't keep. Pages saved by 2004 keep the signatures
        people used at the time.</li>
        <li><b>The 2001 launch forum</b> (aoforums.funcom.com, frozen in September 2001): {launch:N0} threads, from the
        beta in May 2001 to that September. The Wayback Machine saved very little of it; it had 34,458 threads.</li>
        </ul>
        <p>Every thread links to the saved copy it was rebuilt from.</p>

        <h3>How the boards are arranged</h3>
        <p>Every thread is shown on the board list of August 2002. Threads from the launch forum, and from the boards
        Funcom introduced in February 2003, are placed on the 2002 board that matches: the 2003 Atlantean and Rimor
        "Buzz" boards on Role-Playing, which they replaced; the Profession Academies on their professions; New Arrivals on
        Beginners' Corner; Knowledge Database on The World of Rubi-Ka; Tech Help on Other Technical Issues; and so on.
        Shade and Keeper, the professions added with Shadowlands in 2003, have boards of their own. The launch forum's
        General board was sorted thread by thread.</p>

        <h3>What didn't survive</h3>
        <ul>
        <li>Most of the launch forum, and some whole boards, which Funcom's archive didn't keep. Their sizes in
        August 2002: {string.Join(", ", missing)}.</li>
        <li>Much of 2003. After the forum was reorganized in February 2003, Funcom's archive kept only part of the new
        boards.</li>
        <li>Pages here and there that the Wayback Machine never saved; threads say where.</li>
        <li>Replies written after 2003 to these threads are left out.</li>
        <li>Pictures and sites. Posts link to the Wayback Machine's copy of each picture and site, as of the post's
        date, or to the <a href="https://anarchaeology.github.io/">Anarchaeology</a> gallery when it has the picture.
        Links to the item databases of the time (AODB, Anarchy Mainframe, antiguardians.org, HackersQuest) go to the
        same item on <a href="https://auno.org/">auno.org</a>, which still uses the game's item numbers; it shows
        each item as it is today. Links to characters' pages on Funcom's site go to people.anarchy-online.com, which
        shows the characters that still exist.
        The archive's signatures, from later years, are left out.</li>
        </ul>

        <h3>Notes</h3>
        <ul>
        <li>The off-topic boards (Non-AO Related, and in 2003 Reet's Retreat) were read thread by thread: the ones
        about the game are here, on Non-AO Related; people's personal chatter is left out on purpose.</li>
        <li>Times are GMT, as the forum showed them in 2002.</li>
        <li>Under each name, as on the forum: the member's avatar, the last one the saved pages show for them;
        "Registered", the month they joined as the forum showed it, or the month of their first post here where no
        saved page shows it; and "Posts", how many of their posts are in this archive. That is fewer than the forum
        counted, since some boards and threads didn't survive.</li>
        <li>Funcom's archive put runs of underscores in front of many names ("_____________Garnock"); they're shown
        without, as the forum showed them.</li>
        </ul>

        <h3>Removal</h3>
        <p>The posts are their writers' own. If you wrote something here and want it taken down, {issues}.</p>
        <p>Not affiliated with Funcom. Anarchy Online is a trademark of Funcom.</p>
        </div>

        """ + Footer("");
}

sealed class Board(string category, string name, string description, int aug2002, string later)
{
    public string Category = category, Name = name, Description = description, Later = later;
    // Profession boards also carry the name they got in February 2003: "Martial Artist (Dojo)"
    public string Shown => Later == "" ? Name : $"{Name} ({Later})";
    public int Aug2002 = aug2002, PostCount;
    public List<ThreadInfo> Threads = [];
    public string Slug => Regex.Replace(Name.ToLowerInvariant().Replace("'", ""), "[^a-z0-9]+", "-").Trim('-');
}

sealed class ThreadInfo
{
    public string Key = "", Src = "", Title = "", Starter = "", LastAuthor = "";
    public Board Board = null!;
    public int Posts, Pages, Icon;
    public DateTime First, Last;
}

partial class Program
{
    const string Robots = """
        # Anarchy Online forum archive: search engines welcome. Crawlers that collect AI training data are asked to stay out.
        User-agent: GPTBot
        User-agent: ClaudeBot
        User-agent: CCBot
        User-agent: Google-Extended
        User-agent: Bytespider
        Disallow: /

        User-agent: *
        Allow: /

        """;

    // The 2002 forum: brown page, gold text, orange links, black-bordered tables, Verdana.
    const string Css = """
        :root { color-scheme: dark; }
        html { -webkit-text-size-adjust: 100%; }
        body { margin: 0; background: #574544; color: #ffcc66; font: 13px Verdana, Arial, Helvetica, sans-serif; }
        a:link, a:visited, a:active { color: #ff9933; }
        a:hover { color: #ffffcc; }
        img { border: 0; }
        .small { font-size: 10px; }
        .right { text-align: right; }
        .left { text-align: left; }
        .page { width: 95%; margin: 10px auto; }
        table { border-collapse: separate; }
        .top { width: 100%; border-spacing: 0; background: url(images/menu_background.gif); }
        .top td { padding: 0; }
        .top .toplinks { text-align: right; vertical-align: bottom; white-space: nowrap; padding: 0 6px 4px 0; }
        .content { padding: 10px; }
        .navbar { margin: 4px 0 10px; }
        .navbar img { vertical-align: middle; }
        .badge { margin-left: 6px; }
        .welcome { width: 100%; border-spacing: 0; }
        .welcome td { padding: 2px; vertical-align: top; }
        .welcome hr, .about hr { border: 0; border-top: 1px solid #333333; }
        .tborder { width: 100%; background: #000; border-spacing: 1px; }
        .tborder td { padding: 4px; text-align: center; }
        .tborder td.left, .tborder td.body, .tborder td.author, .tborder td.gap { text-align: left; }
        .tborder td.right { text-align: right; }
        .thead td { background: #666666; color: #fff; font-size: 10px; font-weight: bold; white-space: nowrap; }
        .cat td { background: #333333; color: #fff; text-align: left !important; }
        .alt1 { background: #574544; }
        .alt2 { background: #504040; }
        .wide { width: 80%; }
        .icon { width: 1%; }
        .num { white-space: nowrap; }
        .note { color: #e0b070; font-style: italic; }
        .lastpost { white-space: nowrap; text-align: right !important; }
        .lastpost img { vertical-align: middle; }
        .time { color: #ffcc66; }
        .boarddesc { margin: -4px 0 10px; }
        .pagenav { text-align: right; margin: 6px 0; }
        .threadnav { width: 100%; border-spacing: 0; margin: 4px 0; }
        .threadnav td { padding: 2px 0; }
        .title { width: 70%; }
        .mobile { display: none; }
        .posts .author { width: 175px; vertical-align: top; }
        .posts .body { vertical-align: top; }
        .posts .name { font-size: 13px; }
        .avatar { margin-top: 4px; }
        .userinfo { margin: 8px 0 0; }
        .posttitle { margin-bottom: 8px; }
        .posttitle img { vertical-align: middle; }
        .message { overflow-wrap: anywhere; }
        .message blockquote { margin: 10px 40px; }
        .message blockquote hr { border: 0; border-top: 1px solid #333333; }
        .message pre { white-space: pre-wrap; margin: 0; font: 12px "Courier New", monospace; }
        .message .center { text-align: center; }
        .message .right { text-align: right; }
        .smilie { vertical-align: middle; }
        .sig { margin-top: 12px; overflow-wrap: anywhere; }
        .edited { margin: 12px 0 0; }
        .postfoot td { height: 16px; white-space: nowrap; }
        .postfoot img { vertical-align: middle; }
        .gap { background: #333333; color: #fff; font-style: italic; }
        .tz { margin-top: 10px; }
        .footer { text-align: center; }
        .pagefind-ui { --pagefind-ui-scale: .85; --pagefind-ui-primary: #ff9933; --pagefind-ui-text: #ffcc66;
          --pagefind-ui-background: #504040; --pagefind-ui-border: #000; --pagefind-ui-tag: #333333;
          --pagefind-ui-border-width: 1px; --pagefind-ui-border-radius: 0; --pagefind-ui-font: Verdana, Arial, Helvetica, sans-serif; }
        .pagefind-ui mark { background: none; color: #ffffcc; font-weight: bold; }
        .pagefind-ui__result-link { color: #ff9933 !important; }
        .about { max-width: 760px; line-height: 1.5; }
        .about h2 { font-size: 16px; }
        .about h3 { font-size: 13px; margin-top: 20px; }
        @media (max-width: 700px) {
          .page { width: auto; margin: 0; }
          .content { padding: 8px 6px; }
          .top .logo img { width: 100%; max-width: 380px; height: auto; display: block; }
          .top .toplinks { padding: 4px; }
          .tborder td.num, .tborder td.starter, .thead .num, .thead .starter { display: none; }
          .tborder td.lastpost { white-space: normal; }
          .threads .lastpost, .threads .thead td:last-child { display: none; }
          .threads .icon { display: none; }
          .threads .thead td:first-child { text-align: left; }
          .mobile { display: inline; }
          .wide { width: auto; }
          .posts, .posts tbody, .posts tr, .posts td { display: block; width: auto !important; }
          .posts .thead { display: none; }
          .posts .author { border-bottom: 1px solid #000; }
          .posts .author .avatar, .posts .userinfo { display: none; }
          .posts .author br { display: none; }
          .posts .usertitle { margin-left: 6px; }
          .postfoot { display: flex !important; }
          .postfoot td { flex: 1; }
          .message blockquote { margin: 8px 12px; }
        }
        """;
}
