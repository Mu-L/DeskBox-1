# DeskBox International Launch Copy

Replace only bracketed fields after the 1.3.8 release and press page are live. Do not post all channels on the same day.

## Product Hunt

### Name

DeskBox

### Tagline

A free, open-source Windows desktop organizer with native WinUI 3 widgets

### Description

DeskBox brings real-folder-backed file widgets to the Windows 10/11 desktop without replacing Explorer. Collect desktop clutter, map an existing folder without moving it, and keep lightweight search, todo, note, weather, and media tools nearby. It is local-first, requires no account, supports x64 and ARM64, and is open source under GPL-3.0-only.

### Maker comment

Hi Product Hunt,

I’m Tianyu Zhu, a product designer and solo maker. I built DeskBox because my Windows desktop kept filling with screenshots, archives, and temporary documents, but the organizers I tried either felt heavy or changed familiar file behavior.

DeskBox keeps Explorer as the desktop shell. Its file widgets are backed by ordinary folders, and mapping a folder does not move the original files. I used WinUI 3 and .NET 10 so the widgets can use Windows materials and interactions instead of looking like a separate cross-platform layer.

I come from product work rather than traditional software engineering and used AI-assisted development throughout the project. I still own the product decisions, testing, release engineering, and every tradeoff. The code, test history, and x64/ARM64 releases are public.

DeskBox is free and open source. I’d especially value feedback about the first-run experience, multi-monitor behavior, and what feels native or non-native on your Windows setup.

[Press kit]

[GitHub]

## AlternativeTo

### Summary

DeskBox is a free and open-source Windows desktop organizer that adds native-feeling WinUI 3 file and folder widgets without replacing Explorer.

### Description

Organize desktop files in real-folder-backed widgets, or map an existing folder without moving it. DeskBox keeps files as ordinary Windows files and folders and requires no account. It also includes desktop search, todos, quick capture, weather, and media controls. Available for x64 and ARM64 Windows PCs under GPL-3.0-only.

### Suggested categories and alternatives

- Desktop customization
- File organizer
- Productivity tool
- Alternatives: Stardock Fences, Nimi Places, iTop Easy Desktop, Portals

## Reddit — Windows user story

### Title

I built a free, open-source alternative to Fences that feels native on Windows 11

### Body

My desktop kept filling with screenshots, downloads, archives, and files I needed for only a few days. I wanted a lightweight organizer, but I did not want to replace Explorer or put files into a proprietary database.

So I built DeskBox. It adds WinUI 3 file widgets directly to the Windows desktop. A widget can use a normal folder created by DeskBox or map a folder that already exists. The files remain ordinary files, so Explorer, drag-and-drop, rename, delete, and backups keep working the way you expect.

DeskBox is free and open source under GPL-3.0-only. It supports x64 and ARM64, uses Mica and Acrylic, and also includes optional search, todo, quick-note, weather, and media widgets.

Short demo: [demo]

GitHub: <https://github.com/Tianyu199509/DeskBox>

Website: <https://deskbox.fun/en/>

I’m the solo maker and would value honest feedback, especially around Win+D, multi-monitor setups, and whether the interactions feel native on your machine.

## Reddit — open-source and privacy story

### Title

DeskBox is an open-source, local-first desktop organizer for Windows

### Body

DeskBox organizes Windows desktop files through widgets backed by ordinary folders. It does not replace Explorer, require an account, upload the user’s files, or lock them into a private database.

The app is built with C#, WinUI 3, .NET 10, and Windows App SDK. The repository includes automated tests, and direct installers are published for both x64 and ARM64. Weather and update checks use the network for those requested features; the organizer itself is local-first.

Source: <https://github.com/Tianyu199509/DeskBox>

Privacy and product facts: <https://deskbox.fun/en/press/>

I’d welcome review of the file-safety assumptions, updater, and Windows lifecycle behavior. Please report reproducible problems through GitHub Issues.

## Show HN

### Title

Show HN: DeskBox – native WinUI 3 desktop file widgets without replacing Explorer

### Body

Hi HN,

I’m Tianyu Zhu, a product designer rather than a traditional software engineer. I used AI-assisted development to design, build, test, and ship DeskBox, a native Windows desktop organizer.

The technical constraint was to keep Explorer as the shell and keep files as ordinary files. DeskBox attaches WinUI 3 windows to the Explorer desktop layer, recovers when Explorer restarts, handles Win+D and temporary foreground raising, supports per-monitor DPI changes, and preserves native Shell drag-and-drop behavior. File widgets are backed by normal folders; mapped folders are not moved.

The application is C# on .NET 10 and Windows App SDK 2.2, with framework-dependent x64 and ARM64 installers. The source, tests, and issue history are public:

<https://github.com/Tianyu199509/DeskBox>

Demo and technical facts:

<https://deskbox.fun/en/press/>

I’d be glad to discuss what AI-assisted development handled well, where it produced fragile Windows code, and the testing/release work needed to turn the result into a product people can safely try.

## Creator and media email

### Subject

Open-source WinUI 3 desktop organizer for Windows — DeskBox

### Body

Hi [Name],

I’m Tianyu Zhu, the solo maker behind DeskBox, a free and open-source desktop organizer for Windows.

I saw your [specific recent article or video] about [specific Windows utility topic]. DeskBox may be relevant because it adds native-feeling WinUI 3 file and folder widgets directly to the Windows desktop without replacing Explorer. Files remain ordinary local files and folders, and mapped folders stay in their original location.

The main points are:

- WinUI 3 with Mica and Acrylic
- Real-folder-backed file widgets
- Folder mapping without moving existing files
- Local-first storage with no account required
- GPL-3.0-only source
- Native x64 and ARM64 installers

The press kit has verified facts, screenshots, short demonstrations, and direct links:

<https://deskbox.fun/en/press/>

GitHub:

<https://github.com/Tianyu199509/DeskBox>

I can provide additional clean footage, localized screenshots, or technical detail about Explorer layering, drag-and-drop, update behavior, and AI-assisted development.

Best,

Tianyu Zhu

Creator of DeskBox
