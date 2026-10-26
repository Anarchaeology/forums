// ExtractForum.cs
// Reads every saved page of the Anarchy Online forums from the harvest folder and writes one clean record per
// thread for BuildForum.cs. No network access. Three sources, each with its own page layout:
//   pages\   Funcom's forum archive as the Wayback Machine saved it in 2025 (vBulletin 4)
//   live\    pages the Wayback Machine saved from the live forum in 2001-2004 (vBulletin 2.0, 2.2 and 3)
//   launch\  the 2001 launch forum, aoforums.funcom.com (vBulletin 2.0.3 thread pages and printable pages)
// Every thread is placed on the 2002 board list (data\boards.tsv) using data\board_map.tsv and, for the launch
// forum's General board, data\launch_general_sort.tsv. Posts after 2003 are left out (counted per thread).
//
// Post text is cleaned down to simple formatting (bold, italic, colours, lists, quotes, code). Links are rewritten:
// links to threads and posts in the archive point at our own pages (as @t:KEY and @p:POSTID, which BuildForum.cs
// resolves), smilies at our copies (@img/...), pictures in the Anarchaeology gallery at the gallery, and every
// other link at the Wayback Machine's copy nearest the post's date.
// Signatures are kept only from the live and launch pages: the archive shows today's signatures, not 2001-2003's.
//
// Needs the .NET 10 SDK. Run from this tools folder:
//   dotnet run ExtractForum.cs -- HARVESTFOLDER GALLERY_PHOTOS_JSON OUT.jsonl.gz
// OUT.jsonl.gz: one JSON object per line, one line per thread, posts included.

#:property PublishAot=false
#:property NoWarn=IL2026;IL3050

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length < 3) { Console.Error.WriteLine("Usage: dotnet run ExtractForum.cs -- HARVESTFOLDER GALLERY_PHOTOS_JSON OUT.jsonl.gz"); return 2; }
string harvest = args[0], galleryJson = args[1], outPath = args[2];
var until = new DateTime(2004, 1, 1);
const string OffTopic = "Off-topic\tNon-AO Related";   // kept only thread by thread (data\offtopic_review.tsv)

// ---- Board list and mapping ----
var boards = new HashSet<string>();   // "Category\tBoard"
var boardOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // board name -> "Category\tBoard"
foreach (var f in Tsv("data/boards.tsv")) { boards.Add(f[0] + "\t" + f[1]); boardOf[f[1]] = f[0] + "\t" + f[1]; }
var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var f in Tsv("data/board_map.tsv"))
{
    string target = f[1] + "\t" + f[2];
    if (!boards.Contains(target)) { Console.Error.WriteLine($"board_map.tsv: unknown 2002 board {f[1]} > {f[2]}"); return 1; }
    map[f[0]] = target;
}
var launchGeneral = Tsv("data/launch_general_sort.tsv").ToDictionary(f => f[0], f => f[1] + "\t" + f[2]);
foreach (var t in launchGeneral.Values) if (!boards.Contains(t)) { Console.Error.WriteLine($"launch_general_sort.tsv: unknown board {t}"); return 1; }

// ---- Gallery pictures (for links to pictures the gallery has), matched by address, ignoring case ----
var gallery = new Dictionary<string, string>();
using (var doc = JsonDocument.Parse(File.ReadAllText(galleryJson)))
    foreach (var p in doc.RootElement.GetProperty("photos").EnumerateArray())
        gallery.TryAdd(LooseKey(p.GetProperty("u").GetString()!), p.GetProperty("id").GetString()!);

// ---- Pictures linked in posts that the Wayback Machine never saved, or saved (data\picture_copies.tsv) ----
// A link to a picture with no saved copy would only reach the Wayback Machine's "not archived" page, so it becomes
// plain text; a saved one links straight to its copy.
// picture_copies.tsv is the check of October 2025; picture_copies_added.tsv covers pictures on pages added since.
var pictureCopies = new Dictionary<string, (string Ts, string Url)>();
foreach (string list in new[] { "data/picture_copies.tsv", "data/picture_copies_added.tsv" })
    if (File.Exists(list))
        foreach (var f in Tsv(list)) pictureCopies.TryAdd(LooseKey(f[0]), (f.Length > 1 ? f[1] : "", f.Length > 2 ? f[2] : ""));

// ---- Pass 1: which threads and posts exist, so links between them can point at our pages ----
string archiveDir = Path.Combine(harvest, "pages"), liveDir = Path.Combine(harvest, "live", "pages"), launchDir = Path.Combine(harvest, "launch", "pages");
var postIdRx = new Regex(@"id=""post_(\d+)""|<a name=""post(\d+)"">", RegexOptions.Compiled);
var threadKeys = new HashSet<string>();
var postThread = new Dictionary<long, string>();
var archiveFiles = Directory.GetFiles(archiveDir, "*.html.gz").GroupBy(f => Path.GetFileName(f).Split('_')[0]).ToDictionary(g => g.Key, g => g.ToList());
var liveFiles = Directory.GetFiles(liveDir, "*.html.gz").GroupBy(f => Path.GetFileName(f).Split('_')[0]).Where(g => !archiveFiles.ContainsKey(g.Key)).ToDictionary(g => g.Key, g => g.ToList());
// later\: 2001-2003 threads the Wayback Machine saved from the live forum only in 2005 or later
string laterDir = Path.Combine(harvest, "later", "pages");
var laterFiles = Directory.Exists(laterDir)
    ? Directory.GetFiles(laterDir, "*.html.gz").GroupBy(f => Path.GetFileName(f).Split('_')[0])
        .Where(g => !archiveFiles.ContainsKey(g.Key) && !liveFiles.ContainsKey(g.Key)).ToDictionary(g => g.Key, g => g.ToList())
    : new Dictionary<string, List<string>>();
var launchFiles = Directory.GetFiles(launchDir, "*.html.gz").GroupBy(f => Path.GetFileName(f).Split('_')[0]).ToDictionary(g => "l" + g.Key, g => g.ToList());
foreach (var (key, files) in archiveFiles.Concat(liveFiles).Concat(laterFiles).Concat(launchFiles))
{
    threadKeys.Add(key);
    if (key.StartsWith('l')) continue;   // the launch forum's post ids are its own; links to them use thread ids
    foreach (var file in files)
        foreach (Match m in postIdRx.Matches(ReadGz(file)))
            postThread.TryAdd(long.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value), key);
}
Console.WriteLine($"pass 1: {archiveFiles.Count} archive threads, {liveFiles.Count} live-forum threads, {launchFiles.Count} launch threads, {postThread.Count} post ids");

// ---- Pass 2: read every thread ----
var stats = new Dictionary<string, int>();
void Count(string k, int n = 1) => stats[k] = stats.GetValueOrDefault(k) + n;
var unmapped = new Dictionary<string, int>();
var leftOut = new List<string>();
var savedAt = new Dictionary<string, (string Ts, string Url)>();
foreach (var f in Tsv(Path.Combine(harvest, "live", "pages.tsv"))) if (f.Length >= 5) savedAt[f[0] + "_" + f[2]] = (f[3], f[4]);
if (File.Exists(Path.Combine(harvest, "later", "pages.tsv")))
    foreach (var f in Tsv(Path.Combine(harvest, "later", "pages.tsv"))) if (f.Length >= 5) savedAt.TryAdd(f[0] + "_" + f[2], (f[3], f[4]));
foreach (var f in Tsv(Path.Combine(harvest, "live_pages.tsv"))) if (f.Length >= 4) savedAt.TryAdd(f[0] + "_" + f[1], (f[2], f[3]));
// Each thread links to the Wayback Machine's copy of its first page, so readers can check the source.
var archiveSaved = new Dictionary<string, string>();
foreach (var f in Tsv(Path.Combine(harvest, "pages.tsv")))
    if (f.Length >= 4 && f[2] == "1") archiveSaved[f[0]] = $"https://web.archive.org/web/{f[3]}/https://forums-archive.anarchy-online.com/showthread.php?{f[0]}-{f[1]}";
var launchSaved = new Dictionary<string, string>();
foreach (var f in Tsv(Path.Combine(harvest, "launch", "pages.tsv")))
    if (f.Length >= 6 && f[0] != "thread") launchSaved[$"{f[0]}_{f[1]}{f[2]}_{f[3]}"] = $"https://web.archive.org/web/{f[3]}/{f[5]}";

var offTopicVerdict = File.Exists("data/offtopic_review.tsv")
    ? Tsv("data/offtopic_review.tsv").ToDictionary(f => f[0], f => f[1]) : new Dictionary<string, string>();
using var outStream = new GZipStream(File.Create(outPath), CompressionLevel.Optimal);
using var reviewStream = new GZipStream(File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "offtopic.jsonl.gz")), CompressionLevel.Optimal);
var jsonOpts = new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
foreach (var (key, files) in archiveFiles.OrderBy(k => int.Parse(k.Key))) Emit(ReadArchiveThread(key, files));
foreach (var (key, files) in liveFiles.OrderBy(k => int.Parse(k.Key))) Emit(ReadLiveThread(key, files));
foreach (var (key, files) in laterFiles.OrderBy(k => int.Parse(k.Key))) Emit(ReadLiveThread(key, files, laterSave: true));
foreach (var (key, files) in launchFiles.OrderBy(k => int.Parse(k.Key[1..]))) Emit(ReadLaunchThread(key, files));
outStream.Flush();
reviewStream.Flush();

foreach (var (k, v) in stats.OrderBy(s => s.Key)) Console.WriteLine($"  {k}: {v}");
if (leftOut.Count > 0)
{
    Console.WriteLine("live-forum and launch threads left out:");
    foreach (var l in leftOut) Console.WriteLine("  " + l);
}
if (unmapped.Count > 0)
{
    Console.WriteLine("boards with no 2002 board (their threads were left out):");
    foreach (var (b, n) in unmapped.OrderByDescending(u => u.Value)) Console.WriteLine($"  {n,6}  {b}");
}
return 0;

void Emit(ForumThread? t)
{
    if (t is null) return;
    var kept = t.Posts.Where(p => p.When is null || p.When < until).ToList();
    t.Later = t.Posts.Count - kept.Count;
    var first = kept.FirstOrDefault(p => p.When is not null)?.When;
    if (kept.Count == 0 || first is null)
    {
        Count($"{t.Src}: threads left out ({(t.Posts.Count == 0 ? "no posts found" : kept.Count == 0 ? "started after 2003" : "no post dates found")})");
        if (t.Src != "archive") leftOut.Add($"{t.Src} {t.Key} {t.Posts.Count} posts, first date {t.Posts.FirstOrDefault()?.When:yyyy-MM-dd}: {t.Title}");
        return;
    }
    if (t.Board == "")
    {
        Count($"{t.Src}: threads left out (board not mapped)");
        string what = t.Src == "archive" ? t.From : $"{t.From} (thread {t.Key}: {t.Title})";
        unmapped[what] = unmapped.GetValueOrDefault(what) + 1;
        return;
    }
    t.Posts = kept;
    // The off-topic board is kept thread by thread: every one goes to the review file, and only those marked
    // "keep" in data\offtopic_review.tsv (they're about the game) go into the archive.
    if (t.Board == OffTopic)
    {
        WriteThread(t, reviewStream);
        if (offTopicVerdict.GetValueOrDefault(t.Key) != "keep")
        {
            Count($"{t.Src}: off-topic threads left out"); Count($"{t.Src}: off-topic posts left out", kept.Count);
            return;
        }
    }
    Count($"{t.Src}: threads"); Count($"{t.Src}: posts", kept.Count); Count("all: later posts left out", t.Later);
    if (t.Posts.Any(p => p.GapBefore > 0) || t.GapAfter > 0) Count($"{t.Src}: threads with pages that weren't saved");
    WriteThread(t, outStream);
}

void WriteThread(ForumThread t, Stream target)
{
    var buffer = new MemoryStream();
    using (var w = new Utf8JsonWriter(buffer, jsonOpts))
    {
        w.WriteStartObject();
        w.WriteString("key", t.Key); w.WriteString("src", t.Src); w.WriteString("title", t.Title);
        var cb = t.Board.Split('\t'); w.WriteString("cat", cb[0]); w.WriteString("board", cb[1]); w.WriteString("from", t.From);
        w.WriteNumber("later", t.Later); w.WriteNumber("gapAfter", t.GapAfter);
        w.WriteString("saved", t.Saved);
        w.WriteStartArray("posts");
        foreach (var p in t.Posts)
        {
            w.WriteStartObject();
            if (p.Pid > 0) w.WriteNumber("pid", p.Pid);
            w.WriteString("a", p.Author);
            if (p.Uid != "") w.WriteString("uid", p.Uid);
            if (p.UserTitle != "") w.WriteString("ut", p.UserTitle);
            if (p.Avatar != "") w.WriteString("av", p.Avatar);
            if (p.Registered != "") w.WriteString("reg", p.Registered);
            if (p.PostCount != "") w.WriteString("pc", p.PostCount);
            w.WriteString("d", p.When is { } d ? d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : "");
            if (p.Title != "") w.WriteString("t", p.Title);
            if (p.Icon > 0) w.WriteNumber("ic", p.Icon);
            w.WriteString("h", p.Html);
            if (p.Sig != "") w.WriteString("sig", p.Sig);
            if (p.Edit != "") w.WriteString("edit", p.Edit);
            if (p.GapBefore > 0) w.WriteNumber("gap", p.GapBefore);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
    buffer.WriteByte((byte)'\n');
    buffer.Position = 0;
    buffer.CopyTo(target);
}

// ---- Funcom's archive (vBulletin 4) ----
ForumThread? ReadArchiveThread(string key, List<string> files)
{
    var t = new ForumThread { Key = key, Src = "archive" };
    var byPage = new SortedDictionary<int, string>();
    foreach (var f in files) byPage[int.Parse(Path.GetFileName(f).Split('_')[1].Split('.')[0])] = f;
    int total = 1;
    int expected = 1;
    foreach (var (page, file) in byPage)
    {
        string html = ReadGz(file);
        double offset = TimeOffset(html);
        var nav = Rx.ArchiveNavbit.Matches(html).Select(m => Text(m.Groups[1].Value)).ToList();
        if (nav.Count > 1 && t.From == "") t.From = string.Join(" > ", nav.Skip(1));
        if (t.Title == "" && Rx.ArchiveLastNavbit.Match(html) is { Success: true } ln) t.Title = Text(ln.Groups[1].Value);
        if (Rx.PageOf.Match(html) is { Success: true } po) total = Math.Max(total, int.Parse(po.Groups[2].Value));
        int gap = page - expected;
        expected = page + 1;
        var posts = ReadVb4Posts(html, "forums-archive.anarchy-online.com");
        if (posts.Count > 0 && gap > 0) posts[0].GapBefore = gap;
        t.Posts.AddRange(posts.Select(p => Tidy(p, offset, null)));
    }
    t.GapAfter = Math.Max(0, total - (expected - 1));
    t.Board = map.GetValueOrDefault(t.From, "");
    t.Saved = archiveSaved.GetValueOrDefault(key, "");
    return t;
}

// vBulletin 4: Funcom's archive, and the live forum from about 2008
List<Post> ReadVb4Posts(string html, string host)
{
    var list = new List<Post>();
    var starts = Rx.ArchivePost.Matches(html).ToList();
    for (int i = 0; i < starts.Count; i++)
    {
        int s = starts[i].Index, e = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
        string block = html[s..e];
        var p = new Post { Pid = long.Parse(starts[i].Groups[1].Value) };
        if (Rx.ArchiveDate.Match(block) is { Success: true } d) p.When = ParseWhen(d.Groups[1].Value + " " + d.Groups[2].Value, null);
        if (Rx.ArchiveUser.Match(block) is { Success: true } u)
        {
            p.Author = Text(u.Groups[2].Value + u.Groups[3].Value);
            p.Uid = u.Groups[1].Value;
        }
        else p.Author = "Unknown";
        if (Rx.ArchiveUserTitle.Match(block) is { Success: true } ut) p.UserTitle = Text(ut.Groups[1].Value);
        if (Rx.ArchiveAvatar.Match(block) is { Success: true } av) p.Avatar = RaceAvatar(av.Groups[1].Value);
        if (Rx.ArchiveTitle.Match(block) is { Success: true } ti)
        {
            p.Title = Text(ti.Groups[1].Value);
            if (Rx.IconNumber.Match(ti.Groups[1].Value) is { Success: true } ic) p.Icon = int.Parse(ic.Groups[1].Value);
        }
        int b = block.IndexOf("<blockquote class=\"postcontent restore", StringComparison.Ordinal);
        if (b >= 0)
        {
            b = block.IndexOf('>', b) + 1;
            int after = block.IndexOf("<div class=\"after_content\">", b, StringComparison.Ordinal);
            int end = block.LastIndexOf("</blockquote>", after >= 0 ? after : block.Length - 1, StringComparison.Ordinal);
            p.Html = Clean(block[b..(end > b ? end : block.Length)], p.When, false, host, false);
        }
        if (Rx.ArchiveEdited.Match(block) is { Success: true } ed) p.Edit = Text(ed.Groups[1].Value);
        list.Add(p);
    }
    return list;
}

// ---- Pages saved from the live forum: vBulletin 2.0 and 2.2 (2001-2003), 3 (2003-2007), 4 (from about 2008).
// Threads saved after 2004 (later\) show the signatures people used then, not in 2001-2003, so those are left out.
ForumThread? ReadLiveThread(string key, List<string> files, bool laterSave = false)
{
    var t = new ForumThread { Key = key, Src = "live" };
    int total = 1, expected = 1;
    foreach (var file in files.OrderBy(f => int.Parse(Path.GetFileName(f).Split('_')[1].Split('.')[0])))
    {
        string pageKey = Path.GetFileName(file)[..^".html.gz".Length];
        int page = int.Parse(pageKey.Split('_')[1]);
        var (ts, url) = savedAt.GetValueOrDefault(pageKey, ("", ""));
        DateTime? savedOn = ts.Length >= 8 ? DateTime.ParseExact(ts[..8], "yyyyMMdd", CultureInfo.InvariantCulture) : null;
        bool highlight = url.Contains("highlight=", StringComparison.OrdinalIgnoreCase);
        string html = ReadGz(file);
        if (t.Title == "") t.Title = PageTitle(html);
        bool vb4 = Rx.ArchivePost.IsMatch(html);
        if (vb4)
        {
            if (Rx.ArchiveLastNavbit.Match(html) is { Success: true } ln) t.Title = Text(ln.Groups[1].Value);
            var nav = Rx.ArchiveNavbit.Matches(html).Select(m => Text(m.Groups[1].Value)).ToList();
            if (t.From == "" && nav.Count > 0) t.From = nav[^1];
        }
        else
        {
            string head = html[..Math.Max(0, html.IndexOf("<a name=\"post", StringComparison.Ordinal) is int ix and > 0 ? ix : Math.Min(html.Length, 30000))];
            if (t.From == "" && LastBoard(head) is { } bname) t.From = bname;
        }
        total = Math.Max(total, PageCount(html));
        int gap = page - expected;
        expected = page + 1;
        var posts = vb4 ? ReadVb4Posts(html, "forums.anarchy-online.com")
            : html.Contains("<!-- post #") ? ReadVb3Posts(html, savedOn, highlight) : ReadVb2Posts(html, savedOn, highlight, "forums.anarchy-online.com");
        if (laterSave) foreach (var p in posts) p.Sig = "";
        if (posts.Count > 0 && gap > 0) posts[0].GapBefore = gap;
        double offset = TimeOffset(html);
        t.Posts.AddRange(posts.Select(p => Tidy(p, offset, savedOn)));
    }
    t.GapAfter = Math.Max(0, total - (expected - 1));
    if (savedAt.TryGetValue(files.Select(f => Path.GetFileName(f)[..^".html.gz".Length]).Order().First(), out var first) && first.Url != "")
        t.Saved = $"https://web.archive.org/web/{first.Ts}/{first.Url}";
    t.Board = boardOf.GetValueOrDefault(t.From) ?? map.GetValueOrDefault("live: " + t.From, "");
    if (t.From == "") t.From = "(no board on the page)";
    return t;
}

// ---- The 2001 launch forum: thread pages and printable pages, merged ----
ForumThread? ReadLaunchThread(string key, List<string> files)
{
    var t = new ForumThread { Key = key, Src = "launch" };
    // The same post can be in several copies: matched by post id, or (printable pages have none) by author and
    // the start of the text. Copies saved weeks apart can disagree on the time by hours (all say GMT); the earliest
    // saved copy's time is kept.
    var merged = new List<Post>();
    var byId = new Dictionary<long, Post>();
    var byText = new Dictionary<string, Post>();
    var byTime = new Dictionary<string, Post>();
    int total = 1;
    bool havePrint = false;
    foreach (var file in files.OrderBy(f => Path.GetFileName(f).Split('_')[2]))
    {
        string name = Path.GetFileName(file);
        string ts = name.Split('_')[2][..14];
        var savedOn = DateTime.ParseExact(ts[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
        string html = ReadGz(file);
        double offset = TimeOffset(html);
        if (t.Title == "") t.Title = PageTitle(html);
        if (LastBoard(html[..Math.Min(html.Length, html.Contains("<a name=\"post") ? html.IndexOf("<a name=\"post", StringComparison.Ordinal) : 6000)]) is { } b && t.From == "") t.From = b;
        List<Post> posts;
        if (name.Contains("_print"))
        {
            havePrint = true;
            posts = ReadPrintPosts(html);
        }
        else
        {
            total = Math.Max(total, Rx.PageNumber.Matches(html).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(1).Max());
            posts = ReadVb2Posts(html, savedOn, false, "aoforums.funcom.com");
        }
        foreach (var raw in posts)
        {
            var p = Tidy(raw, offset, savedOn);
            string norm = Regex.Replace(Text(p.Html), @"\W+", "").ToLowerInvariant();
            string textKey = p.Author + "|" + norm[..Math.Min(80, norm.Length)];
            string timeKey = p.Author + "|" + p.When?.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            var old = p.Pid > 0 && byId.TryGetValue(p.Pid, out var a) ? a : byText.GetValueOrDefault(textKey) ?? byTime.GetValueOrDefault(timeKey);
            if (old is null) { merged.Add(p); old = p; }
            else
            {   // keep what each copy knows: thread pages have titles, avatars and post ids; printable pages don't
                if (old.Pid == 0) old.Pid = p.Pid;
                if (old.Uid == "") old.Uid = p.Uid;
                if (old.UserTitle == "") old.UserTitle = p.UserTitle;
                if (old.Avatar == "") old.Avatar = p.Avatar;
                if (old.Registered == "") old.Registered = p.Registered;
                if (old.PostCount == "") old.PostCount = p.PostCount;
                if (old.Icon == 0) old.Icon = p.Icon;
                if (old.Title == "") old.Title = p.Title;
                if (old.Html.Length < p.Html.Length) old.Html = p.Html;
                if (old.Sig == "") old.Sig = p.Sig;
                if (old.Edit == "") old.Edit = p.Edit;
            }
            if (old.Pid > 0) byId[old.Pid] = old;
            byText[textKey] = old;
            byTime[timeKey] = old;
        }
    }
    t.Posts = merged.OrderBy(p => p.When).ToList();
    // A printable page holds the whole thread; without one, later pages of a long thread weren't saved.
    if (!havePrint && total > 1) t.GapAfter = total - 1;
    var best = files.Select(f => Path.GetFileName(f)[..^".html.gz".Length]).OrderBy(n => n.Contains("_print") ? 0 : 1).ThenBy(n => n).First();
    t.Saved = launchSaved.GetValueOrDefault(best, "");
    string id = key[1..];
    // General has no 2002 board, so its threads are placed one by one (as is the one thread whose page names no board)
    t.Board = t.From.Equals("General", StringComparison.OrdinalIgnoreCase) || t.From == "" ? launchGeneral.GetValueOrDefault(id, "")
        : boardOf.GetValueOrDefault(t.From) ?? map.GetValueOrDefault("launch: " + t.From, "");
    t.From = "2001 launch forum > " + t.From;
    return t;
}

List<Post> ReadVb2Posts(string html, DateTime? savedOn, bool highlight, string host)
{
    var list = new List<Post>();
    var anchors = Rx.Vb2Anchor.Matches(html).ToList();
    for (int i = 0; i < anchors.Count; i++)
    {
        int s = anchors[i].Index, e = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;
        string block = html[s..e];
        var p = new Post { Pid = long.Parse(anchors[i].Groups[1].Value) };
        if (Rx.Vb2UserId.Match(block) is { Success: true } uid) p.Uid = uid.Groups[1].Value;
        if (Rx.Vb2Author.Match(block) is { Success: true } a)
        {
            p.Author = Text(a.Groups[1].Value);
            if (Rx.Vb2UserTitle.Match(block, a.Index + a.Length) is { Success: true } ut && ut.Index - (a.Index + a.Length) < 40) p.UserTitle = Text(ut.Groups[1].Value);
        }
        var bm = Rx.Vb2BodyStart.Match(block);
        if (!bm.Success) continue;
        string profile = block[..bm.Index];
        if (Rx.AnyAvatar.Match(profile) is { Success: true } av) p.Avatar = RaceAvatar(av.Groups[1].Value);
        if (Rx.Registered.Match(profile) is { Success: true } rg) p.Registered = rg.Groups[1].Value;
        if (Rx.PostCount.Match(profile) is { Success: true } pc) p.PostCount = pc.Groups[1].Value;
        if (Rx.Vb2Title.Match(profile) is { Success: true } ti)
        {
            p.Icon = ti.Groups[1].Success ? int.Parse(ti.Groups[1].Value) : 0;
            p.Title = Text(ti.Groups[2].Value);
        }
        int b = bm.Index + bm.Length;
        int end = block.IndexOf("</font></p>", b, StringComparison.Ordinal);
        if (end < 0) end = block.Length;
        int report = block.IndexOf("report.php", end, StringComparison.Ordinal);
        report = report < 0 ? block.Length : Math.Max(end, block.LastIndexOf('<', report));   // the start of that link's tag
        string rest = block[end..report];
        string dateArea = Regex.Replace(block[report..], "<[^>]+>", " ");
        p.When = ParseWhen(dateArea, savedOn);
        p.Html = Clean(block[b..end], p.When, highlight, host, false);
        int rule = rest.IndexOf("__________________", StringComparison.Ordinal);
        if (rule >= 0) p.Sig = Clean(rest[(rule + 18)..], p.When, highlight, host, true);
        if (Rx.Vb2Edited.Match(rest) is { Success: true } ed) p.Edit = Text(ed.Value);
        list.Add(p);
    }
    return list;
}

List<Post> ReadVb3Posts(string html, DateTime? savedOn, bool highlight)
{
    var list = new List<Post>();
    foreach (Match m in Rx.Vb3Post.Matches(html))
    {
        string block = m.Groups[2].Value;
        var p = new Post { Pid = long.Parse(m.Groups[1].Value) };
        if (Rx.Vb3UserId.Match(block) is { Success: true } uid) p.Uid = uid.Groups[1].Value;
        if (Rx.Vb3Author.Match(block) is { Success: true } a)
        {
            p.Author = Text(a.Groups[1].Value);
            if (Rx.Vb3SmallFont.Match(block, a.Index + a.Length) is { Success: true } ut) p.UserTitle = Text(ut.Groups[1].Value);
        }
        if (Rx.AnyAvatar.Match(block) is { Success: true } av) p.Avatar = RaceAvatar(av.Groups[1].Value);
        if (Rx.JoinDate.Match(block) is { Success: true } jd) p.Registered = jd.Groups[1].Value;
        if (Rx.PostCount.Match(block) is { Success: true } pc) p.PostCount = pc.Groups[1].Value;
        if (Rx.Vb3Title.Match(block) is { Success: true } ti)
        {
            if (Rx.IconNumber.Match(ti.Groups[1].Value) is { Success: true } ic) p.Icon = int.Parse(ic.Groups[1].Value);
            if (Rx.Strong.Match(ti.Groups[1].Value) is { Success: true } st) p.Title = Text(st.Groups[1].Value);
        }
        if (Rx.Vb3Date.Match(block) is { Success: true } d) p.When = ParseWhen(Regex.Replace(d.Groups[1].Value, "<[^>]+>", " "), savedOn);
        if (Rx.Vb3Body.Match(block) is { Success: true } body) p.Html = Clean(body.Groups[1].Value, p.When, highlight, "forums.anarchy-online.com", false);
        if (Rx.Vb3Sig.Match(block) is { Success: true } sig)
            p.Sig = Clean(Regex.Replace(sig.Groups[1].Value, @"^\s*__________________\s*(<br\s*/?>)?", ""), p.When, highlight, "forums.anarchy-online.com", true);
        if (Rx.Vb3Edited.Match(block) is { Success: true } ed) p.Edit = Text(ed.Groups[1].Value);
        list.Add(p);
    }
    return list;
}

List<Post> ReadPrintPosts(string html)
{
    var list = new List<Post>();
    foreach (Match m in Rx.PrintPost.Matches(html))
    {
        var p = new Post { Author = Text(m.Groups[1].Value), When = ParseWhen(m.Groups[2].Value, null) };
        string rest = m.Groups[3].Value;
        if (Rx.PrintTitle.Match(rest) is { Success: true } ti)
        {
            p.Icon = ti.Groups[1].Success ? int.Parse(ti.Groups[1].Value) : 0;
            p.Title = Text(ti.Groups[2].Value);
            rest = rest[(ti.Index + ti.Length)..];
        }
        int sig = rest.IndexOf("<!-- BEGIN TEMPLATE: postbit_signature -->", StringComparison.Ordinal);
        string body = sig >= 0 ? rest[..sig] : rest;
        body = Regex.Replace(body, @"^\s*<p>|</p>\s*$", "");
        p.Html = Clean(body, p.When, false, "aoforums.funcom.com", false);
        if (sig >= 0)
        {
            string s = rest[sig..];
            int rule = s.IndexOf("__________________", StringComparison.Ordinal);
            if (rule >= 0) p.Sig = Clean(s[(rule + 18)..], p.When, false, "aoforums.funcom.com", true);
        }
        list.Add(p);
    }
    return list;
}

// Every page says which time zone its times are in ("All times are GMT +1"): Funcom's archive shows GMT +1, the
// 2001-2003 forum GMT. Times are stored as GMT, as the 2002 forum showed them. Funcom's archive also put runs of
// underscores in front of many names ("_____________Garnock"); the forum showed them without.
Post Tidy(Post p, double offset, DateTime? savedOn)
{
    if (p.When is { } w) p.When = w.AddHours(-offset);
    p.Author = CleanName(p.Author);
    p.Html = p.Html.Replace("\r", "");
    p.Sig = p.Sig.Replace("\r", "");
    if (p.Edit != "")
    {   // kept as "name|time", and only for edits made before 2004
        var m = Rx.EditNote.Match(p.Edit);
        DateTime? edited = m.Success ? ParseWhen(m.Groups[2].Value, savedOn)?.AddHours(-offset) : null;
        p.Edit = edited is { } e && e < until ? CleanName(m.Groups[1].Value.Trim()) + "|" + e.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : "";
    }
    return p;
}

static double TimeOffset(string html) =>
    Rx.TimeZone.Match(html) is { Success: true } tz && tz.Groups[1].Success
        ? double.Parse(tz.Groups[1].Value.Replace(" ", ""), CultureInfo.InvariantCulture) : 0;

static string CleanName(string name) => Regex.Replace(name, "^_{2,}", "");

// ---- Cleaning post text ----
string Clean(string html, DateTime? when, bool highlight, string host, bool isSig)
{
    // vBulletin 4 quote and code boxes become the 2002 forum's own: "quote:" over a rule, then the quote.
    html = Rx.PostedBy.Replace(html, m => $"<i>Originally posted by {m.Groups[1].Value}</i><br>");
    html = Rx.CodeBox.Replace(html, m => $"<blockquote class=\"code\"><span class=\"small\">code:</span><hr><pre>{m.Groups[1].Value}</pre><hr></blockquote>");
    var sb = new StringBuilder(html.Length + 64);
    var stack = new List<(string Name, string Close)>();
    string? skip = null;
    int pos = 0;
    foreach (Match m in Rx.Tag.Matches(html))
    {
        if (skip is null) AppendText(pos, m.Index - pos);
        pos = m.Index + m.Length;
        if (m.Value.StartsWith("<!--")) continue;
        bool closing = m.Groups[1].Length > 0;
        string name = m.Groups[2].Value.ToLowerInvariant(), attrs = m.Groups[3].Value;
        if (skip is not null) { if (closing && name == skip) skip = null; continue; }
        if (!closing && name is "script" or "style" or "iframe" or "object" or "embed" or "noscript" or "textarea" or "select" or "map")
        { skip = name; continue; }
        if (closing) { CloseTag(name); continue; }
        switch (name)
        {
            case "br": sb.Append("<br>"); break;
            case "hr": sb.Append("<hr>"); break;
            case "img": sb.Append(Image(Attr(attrs, "src"), when)); break;
            case "b" or "strong": Open(name, "<b>", "</b>"); break;
            case "i" or "em": Open(name, "<i>", "</i>"); break;
            case "u": Open(name, "<u>", "</u>"); break;
            case "s" or "strike" or "del": Open(name, "<s>", "</s>"); break;
            case "sub" or "sup": Open(name, $"<{name}>", $"</{name}>"); break;
            case "ul": Open(name, "<ul>", "</ul>"); break;
            case "ol":
                string cls = (Attr(attrs, "class") + " " + Attr(attrs, "type")).ToLowerInvariant();
                string type = cls.Contains("lower-alpha") || cls.Trim() == "a" ? " type=\"a\"" : cls.Contains("upper-alpha") || cls.Trim() == "A" ? " type=\"A\"" : cls.Contains("roman") ? " type=\"i\"" : "";
                Open(name, $"<ol{type}>", "</ol>"); break;
            case "li": Open(name, "<li>", "</li>"); break;
            case "pre": Open(name, "<pre>", "</pre>"); break;
            case "code": Open(name, "<code>", "</code>"); break;
            case "blockquote": Open(name, "<blockquote>", "</blockquote>"); break;
            case "p": Open(name, "<p>", "</p>"); break;
            case "center": Open(name, "<div class=\"center\">", "</div>"); break;
            case "font" or "span":
            {
                string color = Attr(attrs, "color");
                if (color == "" && Rx.StyleColor.Match(attrs) is { Success: true } sc) color = sc.Groups[1].Value;
                bool small = Attr(attrs, "size") == "1";
                if (highlight && (color.Equals("red", StringComparison.OrdinalIgnoreCase) || Attr(attrs, "class") == "highlight")) color = "";
                if (!Rx.SafeColor.IsMatch(color)) color = "";
                if (color == "" && !small) { Open(name, "", ""); break; }
                Open(name, $"<span{(small ? " class=\"small\"" : "")}{(color != "" ? $" style=\"color:{color}\"" : "")}>", "</span>");
                break;
            }
            case "div":
            {
                string c = Attr(attrs, "class");
                if (c == "bbcode_quote") Open(name, "<blockquote class=\"quote\"><span class=\"small\">quote:</span><hr>", "<hr></blockquote>");
                else if (c is "bbcode_container" or "quote_container" or "bbcode_quote_container" or "message") Open(name, "", "");
                else
                {
                    string align = (Attr(attrs, "align") + " " + Attr(attrs, "style")).ToLowerInvariant();
                    Open(name, align.Contains("center") ? "<div class=\"center\">" : align.Contains("right") ? "<div class=\"right\">" : "<div>", "</div>");
                }
                break;
            }
            case "a":
            {
                string? href = Link(Attr(attrs, "href"), when, host);
                if (href is null) Open(name, "", "");
                else Open(name, $"<a href=\"{WebUtility.HtmlEncode(href)}\">", "</a>");
                break;
            }
            default: break;   // tables, forms and anything else: the tag goes, its text stays
        }
    }
    if (skip is null) AppendText(pos, html.Length - pos);
    while (stack.Count > 0) { sb.Append(stack[^1].Close); stack.RemoveAt(stack.Count - 1); }
    string result = sb.ToString();
    // drop elements left empty (an empty paragraph, a colour around nothing), then stray line breaks at the ends
    for (string before = ""; before != result;) { before = result; result = Rx.EmptyElement.Replace(result, ""); }
    result = Regex.Replace(result, @"^(\s*<br>)+|(<br>\s*)+$", "").Trim();
    return result;

    // Text between tags; a "<" that doesn't start a tag (a cut-off tag, "<3") is shown as text.
    void AppendText(int start, int length)
    {
        if (length <= 0) return;
        string text = html.Substring(start, length);
        sb.Append(text.Contains('<') ? text.Replace("<", "&lt;") : text);
    }
    void Open(string name, string open, string close) { sb.Append(open); stack.Add((name, close)); }
    void CloseTag(string name)
    {
        int at = stack.FindLastIndex(s => s.Name == name);
        if (at < 0) return;
        for (int i = stack.Count - 1; i >= at; i--) { sb.Append(stack[i].Close); stack.RemoveAt(i); }
    }
}

string Image(string src, DateTime? when)
{
    src = WebUtility.HtmlDecode(src).Trim();
    if (Rx.Smilie.Match(src) is { Success: true } sm) return $"<img src=\"@img/smilies/{sm.Groups[1].Value.ToLowerInvariant()}.gif\" alt=\"\" class=\"smilie\">";
    if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase) || Rx.ForumHost.IsMatch(src)) return "";   // forum buttons and icons
    if (gallery.TryGetValue(LooseKey(src), out string? id)) { Count("links: pictures pointed at the gallery"); return $"<a href=\"https://anarchaeology.github.io/#{id}\">[picture]</a>"; }
    if (pictureCopies.TryGetValue(LooseKey(src), out var copy))
    {
        if (copy.Ts == "") { Count("links: pictures never saved (plain text)"); return "[picture not saved]"; }
        Count("links: pictures, to their saved copy");
        return $"<a href=\"{WebUtility.HtmlEncode($"https://web.archive.org/web/{copy.Ts}/{copy.Url}")}\">[picture]</a>";
    }
    Count("links: pictures pointed at the Wayback Machine");
    return $"<a href=\"{WebUtility.HtmlEncode(Wayback(src, when))}\">[picture]</a>";
}

string? Link(string href, DateTime? when, string host)
{
    href = WebUtility.HtmlDecode(href).Trim();
    if (href == "" || href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;
    bool forumRelative = !href.Contains("://") && Rx.ForumPage.IsMatch(href);
    if (forumRelative || Rx.ForumHost.IsMatch(href))
    {
        var tm = Rx.ThreadLink.Match(href);
        if (!tm.Success) return null;   // profiles, searches, board lists: nothing worth keeping
        // The launch forum's thread and post numbers are its own; a link on a launch page, or to its old address,
        // means a launch thread.
        bool launch = host.Contains("aoforums") || href.Contains("aoforums.funcom.com", StringComparison.OrdinalIgnoreCase);
        var post = Rx.PostParam.Match(href);
        if (!launch && post.Success && postThread.ContainsKey(long.Parse(post.Groups[1].Value)))
        { Count("links: to posts in the archive"); return "@p:" + post.Groups[1].Value; }
        string tid = tm.Groups[1].Success ? tm.Groups[1].Value : tm.Groups[2].Success ? tm.Groups[2].Value : "";
        if (tid != "" && threadKeys.Contains(launch ? "l" + tid : tid))
        { Count("links: to threads in the archive"); return "@t:" + (launch ? "l" + tid : tid); }
        string abs = forumRelative ? $"http://{host}/{href.TrimStart('/')}" : href;
        Count("links: to forum threads we don't have (Wayback)");
        return Wayback(abs, when);
    }
    if (href.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) href = "http://" + href;
    if (!href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !href.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)) return null;
    if (ItemLink(href) is { } item) { Count("links: item databases (now auno.org)"); return item; }
    // Character pages on Funcom's site live on as people.anarchy-online.com, where all servers are now dimension 5
    if (Rx.CharacterBio.Match(href) is { Success: true } bio)
    { Count("links: character pages (now people.anarchy-online.com)"); return $"https://people.anarchy-online.com/character/bio/d/5/name/{bio.Groups[1].Value}"; }
    // GameSpy showed its pictures through a viewer page: asp/image.asp?/interviews/.../4.jpg is interviews/.../4.jpg
    if (gallery.TryGetValue(LooseKey(href), out string? id) || gallery.TryGetValue(LooseKey(Rx.GameSpyViewer.Replace(href, "/")), out id))
    { Count("links: pictures pointed at the gallery"); return "https://anarchaeology.github.io/#" + id; }
    if (pictureCopies.TryGetValue(LooseKey(href), out var copy))
    {
        if (copy.Ts == "") { Count("links: pictures never saved (plain text)"); return null; }
        Count("links: pictures, to their saved copy");
        return $"https://web.archive.org/web/{copy.Ts}/{copy.Url}";
    }
    Count("links: other sites (Wayback)");
    return Wayback(href, when);
}

// The item databases of 2001-2003 (AODB / Anarchy Mainframe, antiguardians.org, HackersQuest, and auno.org's own
// older addresses) all named items by the game's item ID, as auno.org still does, so their links go to auno.org's
// page for that item (with its quality level, when the link gave one). Searches by item name become auno searches.
static string? ItemLink(string url)
{
    var m = Rx.ItemDb.Match(url);
    if (!m.Success) return null;
    var q = m.Groups["query"].Value.Split('&', ';').Select(kv => kv.Split('=', 2)).Where(kv => kv.Length == 2)
        .GroupBy(kv => kv[0].ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First()[1]);
    bool search = q.GetValueOrDefault("cmd") == "search" || m.Value.Contains("search.asp", StringComparison.OrdinalIgnoreCase);
    string? id = q.GetValueOrDefault("aoid") ?? q.GetValueOrDefault("lowid") ?? q.GetValueOrDefault("id");
    if (!search && id is not null && Regex.IsMatch(id, @"^\d+$"))
        return $"https://auno.org/ao/db.php?id={id}" + (q.TryGetValue("ql", out var ql) && Regex.IsMatch(ql, @"^\d+$") ? "&ql=" + ql : "");
    string? name = q.GetValueOrDefault("itemname") ?? q.GetValueOrDefault("iname") ?? (search ? q.GetValueOrDefault("name") : null);
    string plain = name is null ? "" : Uri.UnescapeDataString(name.Replace('+', ' ')).Trim();
    return plain == "" ? null : "https://auno.org/ao/db.php?cmd=search&name=" + Uri.EscapeDataString(plain).Replace("%20", "+");
}

static string Wayback(string url, DateTime? when) =>
    "https://web.archive.org/web/" + (when ?? new DateTime(2002, 1, 1)).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "/" + url;

// An address without its scheme, "www." and port, decoded and in lower case, for matching the same picture
// written different ways (Cloudeh's Big2.jpg linked as big2.jpg).
static string LooseKey(string url)
{
    string k = Regex.Replace(url.Trim(), @"^[a-z]+://", "", RegexOptions.IgnoreCase);
    k = Regex.Replace(k, @"^www\.", "", RegexOptions.IgnoreCase);
    k = Regex.Replace(k, @"^([^/]+):\d+", "$1");
    try { k = Uri.UnescapeDataString(k); } catch (UriFormatException) { }
    return k.ToLowerInvariant();
}

// The forum's avatars were a fixed set: race pictures (nanomale.gif), numbered headshots (40100.jpg) and Leets
// (Leets_0004.jpg). All of them were saved; BuildForum.cs shows each member's last one.
static string RaceAvatar(string src)
{
    var m = Regex.Match(src, @"avatars/((?:professionals/)?[A-Za-z0-9_]+\.(?:gif|jpg|png))(?:\?.*)?$");
    return m.Success ? m.Groups[1].Value : "";
}

string PageTitle(string html)
{
    string title = Text(Rx.Title.Match(html).Groups[1].Value);
    return Regex.Replace(title, @"^(Anarchy Online Bulletin Board|Anarchy Online Community)\s*-\s*|\s*-\s*Anarchy Online Bulletin Board$", "").Trim();
}

string? LastBoard(string head)
{
    var names = Rx.BoardLink.Matches(head).Where(m => !m.Value.Contains("markread", StringComparison.OrdinalIgnoreCase))
        .Select(m => Text(m.Groups[1].Value)).Where(n => n != "" && !n.StartsWith("Mark ", StringComparison.Ordinal)).ToList();
    return names.Count > 0 ? names[^1] : null;
}

static int PageCount(string html)
{
    if (Rx.PagesParen.Match(html) is { Success: true } a) return int.Parse(a.Groups[1].Value);
    if (Rx.PageOf.Match(html) is { Success: true } b) return int.Parse(b.Groups[2].Value);
    return 1;
}

static DateTime? ParseWhen(string text, DateTime? savedOn)
{
    var m = Rx.When.Match(WebUtility.HtmlDecode(text).Replace(' ', ' '));
    if (!m.Success) return null;
    DateTime day;
    if (m.Groups["rel"].Success)
    {
        if (savedOn is null) return null;
        day = m.Groups["rel"].Value == "Today" ? savedOn.Value : savedOn.Value.AddDays(-1);
    }
    else if (m.Groups["nm"].Success)
    {
        int mo = int.Parse(m.Groups["nm"].Value), dd = int.Parse(m.Groups["nd"].Value);
        if (mo is < 1 or > 12 || dd is < 1 or > 31) return null;
        day = new DateTime(int.Parse(m.Groups["ny"].Value), mo, Math.Min(dd, DateTime.DaysInMonth(int.Parse(m.Groups["ny"].Value), mo)));
    }
    else
    {
        string mon = m.Groups["mon"].Value[..3];
        if (!DateTime.TryParseExact($"{mon} {m.Groups["day"].Value} {m.Groups["year"].Value}", "MMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return null;
    }
    if (!m.Groups["h"].Success) return day;
    int h = int.Parse(m.Groups["h"].Value), min = int.Parse(m.Groups["m"].Value), sec = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value) : 0;
    if (m.Groups["ampm"].Success) { h %= 12; if (m.Groups["ampm"].Value.Equals("PM", StringComparison.OrdinalIgnoreCase)) h += 12; }
    return h < 24 && min < 60 && sec < 60 ? day.AddHours(h).AddMinutes(min).AddSeconds(sec) : day;
}

static string Text(string html) =>
    Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Replace(' ', ' '), @"\s+", " ").Trim();

static string Attr(string attrs, string name)
{
    var m = Regex.Match(attrs, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
    return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : "";
}

static string ReadGz(string file)
{
    using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
    var bytes = new MemoryStream();
    gz.CopyTo(bytes);
    var data = bytes.ToArray();
    // The archive's pages are UTF-8; pages saved from the 2001-2004 forum are Windows-1252.
    try { return new UTF8Encoding(false, true).GetString(data); }
    catch (DecoderFallbackException) { return Encoding.Latin1.GetString(data); }
}

static IEnumerable<string[]> Tsv(string path) =>
    File.ReadLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t'));

sealed class ForumThread
{
    public string Key = "", Src = "", Title = "", From = "", Board = "", Saved = "";
    public List<Post> Posts = [];
    public int Later, GapAfter;
}

sealed class Post
{
    public long Pid;
    public string Author = "", Uid = "", UserTitle = "", Avatar = "", Registered = "", PostCount = "", Title = "", Html = "", Sig = "", Edit = "";
    public int Icon, GapBefore;
    public DateTime? When;
}

static class Rx
{
    const RegexOptions C = RegexOptions.Compiled;
    public static readonly Regex Tag = new(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)\b((?:[^>""']|""[^""]*""|'[^']*')*)/?>|<!--[\s\S]*?-->", C);
    public static readonly Regex Title = new(@"<title>([\s\S]*?)</title>", C | RegexOptions.IgnoreCase);
    public static readonly Regex PageOf = new(@"Page (\d+) of (\d+)", C);
    public static readonly Regex PagesParen = new(@"Pages \((\d+)\)", C);
    public static readonly Regex PageNumber = new(@"pagenumber=(\d+)", C);
    public static readonly Regex BoardLink = new(@"href=""[^""]*forumdisplay\.php\?[^""]*?(?:forumid|f)=\d+[^""]*""[^>]*>([\s\S]*?)</a>", C);
    public static readonly Regex IconNumber = new(@"icons/icon(\d+)\.(?:png|gif)", C);
    public static readonly Regex Strong = new(@"<strong>([\s\S]*?)</strong>", C);
    public static readonly Regex AnyAvatar = new(@"<img src=""([^""]*avatars/[^""]+)""", C);
    public static readonly Regex Registered = new(@"Registered: ([A-Z][a-z]{2} \d{4})", C);
    public static readonly Regex JoinDate = new(@"Join Date: ([A-Z][a-z]{2} \d{4})", C);
    public static readonly Regex PostCount = new(@"Posts: ([\d,]+)", C);
    // Funcom's archive (vBulletin 4)
    public static readonly Regex ArchiveNavbit = new(@"<li class=""navbit""><a [^>]*>([\s\S]*?)</a></li>", C);
    public static readonly Regex ArchiveLastNavbit = new(@"<li class=""navbit lastnavbit""><span>([\s\S]*?)</span></li>", C);
    public static readonly Regex ArchivePost = new(@"<li class=""postbit[^""]*"" id=""post_(\d+)"">", C);
    public static readonly Regex ArchiveDate = new(@"<span class=""date"">([^<]*?)(?:&nbsp;)?<span class=""time"">([^<]*)</span>", C);
    public static readonly Regex ArchiveUser = new(@"<a class=""username[^""]*"" href=""member\.php\?(\d+)[^""]*""[^>]*>([\s\S]*?)</a>|<span class=""username[^""]*"">([\s\S]*?)</span>", C);
    // Member numbers: vBulletin 2 "member.php?s=&action=getinfo&userid=N", vBulletin 3 "member.php?s=...&u=N"
    public static readonly Regex Vb2UserId = new(@"member\.php\?[^""]*?action=getinfo&(?:amp;)?userid=(\d+)", C);
    public static readonly Regex Vb3UserId = new(@"class=""bigusername"" href=""member\.php\?[^""]*?[?&;]u=(\d+)", C);
    public static readonly Regex ArchiveUserTitle = new(@"<span class=""usertitle"">([\s\S]*?)</span>", C);
    public static readonly Regex ArchiveAvatar = new(@"<a class=""postuseravatar""[^>]*>\s*<img src=""([^""]+)""", C);
    public static readonly Regex ArchiveTitle = new(@"<h2 class=""title icon""[^>]*>([\s\S]*?)</h2>", C);
    public static readonly Regex ArchiveEdited = new(@"<blockquote class=""postcontent lastedited"">([\s\S]*?)</blockquote>", C);
    // vBulletin 2 (live forum 2001-2003 and the launch forum)
    public static readonly Regex Vb2Anchor = new(@"<a name=""post(\d+)""></a>", C);
    public static readonly Regex Vb2Author = new(@"<font face=""verdana, arial, helvetica"" size=""2""\s*><b>([\s\S]*?)</b></font>", C);
    public static readonly Regex Vb2UserTitle = new(@"<font face=""verdana,arial,helvetica"" size=""1""\s*>([\s\S]*?)</font>", C);
    public static readonly Regex Vb2BodyStart = new(@"<p><font face=""verdana, arial, helvetica"" size=""2""\s*>", C);
    public static readonly Regex Vb2Title = new(@"size=""1""\s*>\s*(?:<img src=""[^""]*?icons/icon(\d+)\.gif""[^>]*>)?\s*<b>([\s\S]*?)</b>\s*</font>\s*$", C);
    public static readonly Regex Vb2Edited = new(@"Last edited by[\s\S]*?(?=</font>|</p>)", C);
    // vBulletin 3 (live forum 2003-2004)
    public static readonly Regex Vb3Post = new(@"<!-- post #(\d+) -->([\s\S]*?)<!-- / post #\1 -->", C);
    public static readonly Regex Vb3Author = new(@"class=""bigusername""[^>]*>([\s\S]*?)</a>", C);
    public static readonly Regex Vb3SmallFont = new(@"<div class=""smallfont"">([\s\S]*?)</div>", C);
    public static readonly Regex Vb3Title = new(@"<!-- icon and title -->([\s\S]*?)<!-- / icon and title -->", C);
    public static readonly Regex Vb3Date = new(@"<!-- status icon and date -->([\s\S]*?)<!-- / status icon and date -->", C);
    public static readonly Regex Vb3Body = new(@"<!-- message -->([\s\S]*?)<!-- / message -->", C);
    public static readonly Regex Vb3Sig = new(@"<!-- sig -->([\s\S]*?)<!-- / sig -->", C);
    public static readonly Regex Vb3Edited = new(@"<!-- edit note -->([\s\S]*?)<!-- / edit note -->", C);
    // Printable pages (launch forum)
    public static readonly Regex PrintPost = new(@"<i>Posted by ([\s\S]*?) on ([^<]*?):</i>([\s\S]*?)(?=<!-- END TEMPLATE: printthreadbit -->|<hr>\s*<i>Posted by|</body>)", C);
    public static readonly Regex PrintTitle = new(@"^\s*<p><b>\s*(?:<img src=""[^""]*?icons/icon(\d+)\.gif""[^>]*>)?\s*([\s\S]*?)</b></p>", C);
    // Dates: "Nov 30th, 2001, 18:51:24", "November 15th, 2001 21:51", "11-15-2001 at 09:51 PM", "Today, 09:12 PM"
    public static readonly Regex When = new(@"(?:(?<mon>January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\.? (?<day>\d{1,2})(?:st|nd|rd|th)?,? (?<year>(?:19|20)\d\d)|(?<nm>\d{1,2})-(?<nd>\d{1,2})-(?<ny>(?:19|20)\d\d)|(?<rel>Today|Yesterday))(?:,?\s*(?:at\s*)?(?<h>\d{1,2}):(?<m>\d{2})(?::(?<s>\d{2}))?\s*(?<ampm>AM|PM)?)?", C);
    public static readonly Regex TimeZone = new(@"All times are GMT\s*([+-]\s*\d+(?:\.\d+)?)?", C);
    public static readonly Regex EditNote = new(@"Last edited by (.+?)\s*(?:;|:| on)\s+(.+?)\.?\s*$", C);
    // Post text
    public static readonly Regex PostedBy = new(@"<div class=""bbcode_postedby"">[\s\S]*?Originally Posted by <strong>([\s\S]*?)</strong>[\s\S]*?</div>", C);
    public static readonly Regex CodeBox = new(@"<div class=""bbcode_description"">[^<]*</div>\s*<pre class=""bbcode_code""[^>]*>([\s\S]*?)</pre>", C);
    public static readonly Regex EmptyElement = new(@"<(p|span|div|b|i|u|s|li|ul|ol|blockquote)\b[^>]*>\s*</\1>", C);
    public static readonly Regex StyleColor =new(@"color\s*:\s*([#a-zA-Z0-9]+)", C);
    public static readonly Regex SafeColor = new(@"^(#[0-9a-fA-F]{3,6}|[a-zA-Z]{3,20})?$", C);
    public static readonly Regex Smilie = new(@"/smilies/([A-Za-z0-9_-]+)\.gif$", C);
    public static readonly Regex ForumHost = new(@"^(?:https?://)?(?:www\.)?(?:forums\.anarchy-online\.com|forums-archive\.anarchy-online\.com|(?:archive\.)?aoforums\.funcom\.com)(?::\d+)?(?:/|$)", C | RegexOptions.IgnoreCase);
    public static readonly Regex ForumPage = new(@"^/?[a-z0-9_]+\.php(?:\?|$)", C | RegexOptions.IgnoreCase);
    // showthread.php?s=&threadid=N, ?t=N, ?N-Title (vBulletin 4), or a post: ?p=N / ?postid=N
    public static readonly Regex ThreadLink = new(@"(?:showthread|printthread|showpost)\.php\?(?:(?:[^#]*?[&;])?(?:threadid|t)=(\d+)|(\d+)(?=-|&|$|#)|(?:[^#]*?[&;])?p(?:ostid)?=\d+)", C | RegexOptions.IgnoreCase);
    public static readonly Regex ItemDb = new(@"^https?://(?:www\.)?(?:aodb\.info|aomainframe\.info|64\.106\.154\.149|v020u46eff\.maximumasp\.com|antiguardians\.org|(?:ao\.)?hackersquest\.(?:org|com)|auno\.org)\.?(?::\d+)?/(?:AODB/|ao/)?(?:showitem\.asp|search\.asp|itemdb\.php|show\.php3?|db\.php|aodb\.php)\?(?<query>[^#]*)", C | RegexOptions.IgnoreCase);
    public static readonly Regex GameSpyViewer = new(@"/asp/image\.asp\?/", C | RegexOptions.IgnoreCase);
    public static readonly Regex CharacterBio =new(@"^https?://(?:www\.|community\.|people\.)?anarchy-?online\.com\.?(?::\d+)?/character/bio/d/\d+/name/([^/?#""]+)", C | RegexOptions.IgnoreCase);
    public static readonly Regex PostParam = new(@"[?&;]p(?:ostid)?=(\d+)", C | RegexOptions.IgnoreCase);
}
