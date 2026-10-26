# Tools

How the forum archive is built. The saved forum pages live in a **harvest folder** outside this repository
(`ao_forum_harvest`); the site in the repository root is built from them in two steps. Needs the .NET 10 SDK
(the `.cs` files are single-file apps: `dotnet run Tool.cs -- ...`). Run both from this folder.

## The harvest folder

| Path | What it is |
|---|---|
| `pages\<id>_<page>.html.gz`, `pages.tsv`, `done.tsv` | Funcom's forum archive (vBulletin 4) as the Wayback Machine saved it in 2025, 2001-2003 threads |
| `live\pages\...`, `live\pages.tsv`, `live_pages.tsv` | pages the Wayback Machine saved from the live forum in 2001-2004 (vBulletin 2.0, 2.2, 3) |
| `launch\pages\<id>_<show\|print><page>_<time>.html.gz`, `launch\pages.tsv` | the 2001 launch forum, aoforums.funcom.com (vBulletin 2.0.3) |
| `theme\images\...` | the 2002 forum's theme images (logo, buttons, icons, smilies, race avatars) |

These were fetched with the gallery's `HarvestForum.cs` (archive and live pages) and small one-off scripts, one
request at a time, 5-7 seconds apart.

## 1. Extract

```
dotnet run ExtractForum.cs -- "PATH\TO\ao_forum_harvest" "PATH\TO\anarchaeology.github.io\data\photos.json" "PATH\TO\threads.jsonl.gz"
```

Reads every saved page and writes one JSON line per thread, posts included: author, user title, race avatar,
date (GMT), title, icon, cleaned text, signature (live and launch pages only), edit note. Places each thread on
the 2002 board list using `data\boards.tsv`, `data\board_map.tsv` and `data\launch_general_sort.tsv`, leaves out
posts after 2003, and rewrites links (threads and posts we have; item-database links, which all used the game's
item IDs, to auno.org; character pages to people.anarchy-online.com; the Anarchaeology gallery's pictures; everything else to the Wayback Machine as of the post's
date). The off-topic board is kept thread by thread: only threads marked "keep" in `data\offtopic_review.tsv` stay. Prints counts of everything it read, left out and rewrote.

## 2. Build

```
dotnet run BuildForum.cs -- "PATH\TO\threads.jsonl.gz" "PATH\TO\ao_forum_harvest\theme" .. --repo "https://github.com/Anarchaeology/forums"
```

Writes `index.html`, `forum\`, `thread\`, `about.html`, `search.html`, `style.css`, `robots.txt` and `images\`
into the repository root, and removes thread and board pages an earlier build made that this one doesn't.

## 3. Search index

The search page uses [Pagefind](https://pagefind.app), which indexes the thread pages after the build:
`npx pagefind@1.3.0 --site ..` writes `pagefind\`. Until it has run, the search page says search isn't available.

## Preview

`node PATH\TO\anarchaeology.github.io\tools\scripts\serve-mount.js .. 8776` and open http://localhost:8776.
