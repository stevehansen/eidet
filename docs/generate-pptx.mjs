import pptxgen from "pptxgenjs";

const pptx = new pptxgen();

// ── Theme ──
const DARK = "0D1117";
const DARK2 = "161B22";
const ACCENT = "58A6FF";
const ACCENT2 = "3FB950";
const ACCENT3 = "D2A8FF";
const ACCENT4 = "FFA657";
const ACCENT5 = "FF7B72";
const TEXT = "E6EDF3";
const TEXT_DIM = "8B949E";
const WHITE = "FFFFFF";

pptx.author = "Steve Hansen";
pptx.company = "Eidet";
pptx.subject = "Long-term memory for AI coding agents";
pptx.title = "Eidet — Long-Term Memory for AI Coding Agents";
pptx.layout = "LAYOUT_WIDE"; // 13.33 x 7.5

function addBackground(slide) {
  slide.background = { color: DARK };
}

function titleText(slide, text, opts = {}) {
  slide.addText(text, {
    x: opts.x ?? 0.8,
    y: opts.y ?? 0.3,
    w: opts.w ?? 11.7,
    h: opts.h ?? 0.8,
    fontSize: opts.fontSize ?? 32,
    fontFace: "Segoe UI Semibold",
    color: WHITE,
    bold: true,
    ...opts,
  });
}

function subtitleText(slide, text, opts = {}) {
  slide.addText(text, {
    x: opts.x ?? 0.8,
    y: opts.y ?? 1.1,
    w: opts.w ?? 11.7,
    h: opts.h ?? 0.5,
    fontSize: opts.fontSize ?? 18,
    fontFace: "Segoe UI",
    color: ACCENT,
    ...opts,
  });
}

function bodyText(slide, text, opts = {}) {
  slide.addText(text, {
    x: opts.x ?? 0.8,
    y: opts.y ?? 1.8,
    w: opts.w ?? 11.7,
    h: opts.h ?? 5.0,
    fontSize: opts.fontSize ?? 16,
    fontFace: "Segoe UI",
    color: TEXT,
    valign: "top",
    lineSpacingMultiple: 1.3,
    ...opts,
  });
}

function accentBar(slide, y = 1.05) {
  slide.addShape(pptx.ShapeType.rect, {
    x: 0.8,
    y,
    w: 2.0,
    h: 0.04,
    fill: { color: ACCENT },
  });
}

function footerNote(slide, text) {
  slide.addText(text, {
    x: 0.8,
    y: 6.9,
    w: 11.7,
    h: 0.4,
    fontSize: 11,
    fontFace: "Segoe UI",
    color: TEXT_DIM,
    italic: true,
  });
}

function bulletList(items, opts = {}) {
  return items.map((item) => {
    if (typeof item === "string") {
      return {
        text: item,
        options: {
          fontSize: opts.fontSize ?? 15,
          fontFace: "Segoe UI",
          color: TEXT,
          bullet: { type: "bullet", color: ACCENT },
          indentLevel: opts.indentLevel ?? 0,
          lineSpacingMultiple: 1.4,
          paraSpaceAfter: 4,
        },
      };
    }
    return {
      text: item.text,
      options: {
        fontSize: item.fontSize ?? opts.fontSize ?? 15,
        fontFace: "Segoe UI",
        color: item.color ?? TEXT,
        bold: item.bold ?? false,
        bullet: item.bullet !== false ? { type: "bullet", color: item.bulletColor ?? ACCENT } : undefined,
        indentLevel: item.indentLevel ?? opts.indentLevel ?? 0,
        lineSpacingMultiple: 1.4,
        paraSpaceAfter: item.paraSpaceAfter ?? 4,
      },
    };
  });
}

function codeBlock(slide, code, opts = {}) {
  slide.addText(code, {
    x: opts.x ?? 0.8,
    y: opts.y ?? 3.5,
    w: opts.w ?? 11.7,
    h: opts.h ?? 3.5,
    fontSize: opts.fontSize ?? 12,
    fontFace: "Cascadia Code",
    color: TEXT,
    fill: { color: DARK2 },
    margin: [10, 14, 10, 14],
    valign: "top",
    lineSpacingMultiple: 1.2,
    ...opts,
  });
}

function infoBox(slide, text, opts = {}) {
  slide.addShape(pptx.ShapeType.roundRect, {
    x: opts.x ?? 0.8,
    y: opts.y ?? 5.5,
    w: opts.w ?? 11.7,
    h: opts.h ?? 1.2,
    fill: { color: "1A2332" },
    line: { color: ACCENT, width: 1.5 },
    rectRadius: 0.1,
  });
  slide.addText(text, {
    x: (opts.x ?? 0.8) + 0.2,
    y: opts.y ?? 5.5,
    w: (opts.w ?? 11.7) - 0.4,
    h: opts.h ?? 1.2,
    fontSize: 14,
    fontFace: "Segoe UI",
    color: TEXT,
    valign: "middle",
    lineSpacingMultiple: 1.3,
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 1 — Title
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);

  slide.addShape(pptx.ShapeType.rect, {
    x: 0, y: 0, w: 13.33, h: 0.06,
    fill: { color: ACCENT },
  });

  slide.addText("Eidet", {
    x: 0.8, y: 1.5, w: 11.7, h: 1.2,
    fontSize: 52, fontFace: "Segoe UI Semibold", color: WHITE, bold: true,
  });

  slide.addText("Long-term memory for AI coding agents — local-first, privacy-absolute, works everywhere.", {
    x: 0.8, y: 2.7, w: 11.7, h: 0.7,
    fontSize: 22, fontFace: "Segoe UI", color: ACCENT,
  });

  slide.addText("RavenDB Hybrid Search  |  13 MCP Tools  |  Hooks  |  Web UI  |  3 SDKs  |  Zero Cloud Dependencies", {
    x: 0.8, y: 3.5, w: 11.7, h: 0.5,
    fontSize: 16, fontFace: "Segoe UI", color: TEXT_DIM,
  });

  slide.addText('From "eidetic" — relating to extraordinarily vivid, detailed recall.', {
    x: 0.8, y: 4.3, w: 11.7, h: 0.4,
    fontSize: 14, fontFace: "Segoe UI", color: TEXT_DIM, italic: true,
  });

  slide.addText("eidet.dev  ·  April 2026", {
    x: 0.8, y: 6.2, w: 11.7, h: 0.5,
    fontSize: 14, fontFace: "Segoe UI", color: TEXT_DIM,
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 2 — The Problem
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "The Problem");
  accentBar(slide);
  subtitleText(slide, "Why AI coding assistants need long-term memory");

  const items = bulletList([
    { text: "Context Amnesia", bold: true, color: ACCENT5, bulletColor: ACCENT5 },
    { text: "AI assistants lose ALL learned context between sessions — preferences, decisions, debugging history, codebase insights", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 8 },
    { text: "Flat-File Fragility", bold: true, color: ACCENT5, bulletColor: ACCENT5 },
    { text: "MEMORY.md is manually curated, unsearchable beyond grep, has no semantic recall, grows unwieldy", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 8 },
    { text: "No Cross-Session Learning", bold: true, color: ACCENT5, bulletColor: ACCENT5 },
    { text: "Repeated mistakes, re-asked questions, and re-discovered patterns waste developer time every session", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 8 },
    { text: "No Recall Precision", bold: true, color: ACCENT5, bulletColor: ACCENT5 },
    { text: "Existing solutions either dump everything (token waste) or miss relevant memories (semantic gap)", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 8 },
    { text: "Locked to One Tool", bold: true, color: ACCENT5, bulletColor: ACCENT5 },
    { text: "Memory in Claude Code doesn't help Cursor. Memory in Cursor doesn't help Gemini. No universal standard.", indentLevel: 1 },
  ]);

  slide.addText(items, { x: 0.8, y: 1.8, w: 11.7, h: 5.2, valign: "top" });
}

// ════════════════════════════════════════════════════════════
// SLIDE 3 — The Solution
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "The Solution: Eidet");
  accentBar(slide);
  subtitleText(slide, "A standalone local service — any MCP client gets memory instantly");

  const items = bulletList([
    { text: "Universal — Works Everywhere", bold: true, color: ACCENT2, bulletColor: ACCENT2 },
    { text: "Claude Code, Claude Desktop, Cursor, Windsurf, Cline, or any MCP-compatible client.", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 6 },
    { text: "Local-First — Fully Offline Capable", bold: true, color: ACCENT2, bulletColor: ACCENT2 },
    { text: "RavenDB with built-in embeddings. No cloud, no Python, no external API keys required.", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 6 },
    { text: "Always Running — System Service", bold: true, color: ACCENT2, bulletColor: ACCENT2 },
    { text: "Windows Service / macOS launchd / Linux systemd. Memory is available the instant any client connects.", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 6 },
    { text: "Hybrid Retrieval in a Single Round-Trip", bold: true, color: ACCENT2, bulletColor: ACCENT2 },
    { text: "RavenDB vector search + full-text + metadata filters in one query.", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 6 },
    { text: "< 600 Token Wake-Up Cost", bold: true, color: ACCENT2, bulletColor: ACCENT2 },
    { text: "L0 identity (~50 tokens) + L1 top-K relevant (~500 tokens) at session start.", indentLevel: 1 },
  ]);

  slide.addText(items, { x: 0.8, y: 1.8, w: 11.7, h: 5.2, valign: "top" });
}

// ════════════════════════════════════════════════════════════
// SLIDE 4 — Architecture
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Architecture Overview");
  accentBar(slide);

  // Client boxes at top (multiple)
  const clients = [
    { name: "Claude Code", x: 0.5, color: ACCENT },
    { name: "Cursor", x: 3.0, color: ACCENT3 },
    { name: "Windsurf", x: 5.5, color: ACCENT4 },
    { name: "Any MCP Client", x: 8.0, color: TEXT_DIM },
    { name: "REST / curl", x: 10.5, color: ACCENT2 },
  ];
  clients.forEach((c) => {
    slide.addShape(pptx.ShapeType.roundRect, {
      x: c.x, y: 1.3, w: 2.2, h: 0.55, fill: { color: "1F2937" }, line: { color: c.color, width: 1 }, rectRadius: 0.06,
    });
    slide.addText(c.name, {
      x: c.x, y: 1.3, w: 2.2, h: 0.55, fontSize: 11, fontFace: "Segoe UI Semibold", color: c.color, align: "center", valign: "middle",
    });
  });

  // Arrows
  slide.addText("▼  MCP (stdio/HTTP) or REST API", {
    x: 3.5, y: 1.9, w: 6.3, h: 0.35, fontSize: 11, fontFace: "Segoe UI", color: TEXT_DIM, align: "center",
  });

  // Eidet Service box
  slide.addShape(pptx.ShapeType.roundRect, {
    x: 1.0, y: 2.3, w: 11.3, h: 4.5, fill: { color: "111820" }, line: { color: ACCENT, width: 1 }, rectRadius: 0.1,
  });
  slide.addText("Eidet Service (localhost)", {
    x: 1.2, y: 2.35, w: 5, h: 0.35, fontSize: 13, fontFace: "Segoe UI Semibold", color: ACCENT,
  });

  // Internal boxes
  const boxes = [
    { name: "MCP Server\nstdio + Streamable HTTP", x: 1.3, y: 2.8, w: 3.2, h: 0.75, color: ACCENT3 },
    { name: "REST API\n14 endpoints", x: 4.8, y: 2.8, w: 2.5, h: 0.75, color: ACCENT2 },
    { name: "Scheduler\nMaintenance + Consolidation", x: 7.6, y: 2.8, w: 3.3, h: 0.75, color: ACCENT4 },
    { name: "Eidet.Core\nMemory types, gates, scoring,\nentity extraction, layers", x: 1.3, y: 3.8, w: 5.5, h: 1.0, color: WHITE },
    { name: "Ollama (optional)\nBackground enrichment", x: 7.6, y: 3.8, w: 3.3, h: 1.0, color: TEXT_DIM },
  ];

  boxes.forEach((b) => {
    slide.addShape(pptx.ShapeType.roundRect, {
      x: b.x, y: b.y, w: b.w, h: b.h, fill: { color: DARK2 }, line: { color: b.color, width: 1 }, rectRadius: 0.06,
    });
    slide.addText(b.name, {
      x: b.x + 0.15, y: b.y, w: b.w - 0.3, h: b.h,
      fontSize: 11, fontFace: "Segoe UI", color: b.color, align: "center", valign: "middle", lineSpacingMultiple: 1.15,
    });
  });

  // RavenDB box at bottom
  slide.addShape(pptx.ShapeType.roundRect, {
    x: 1.3, y: 5.2, w: 9.6, h: 0.7, fill: { color: DARK2 }, line: { color: ACCENT2, width: 1.5 }, rectRadius: 0.06,
  });
  slide.addText("RavenDB (External or Embedded)  ·  Built-in Embeddings  ·  Vector + FTS  ·  Corax Engine", {
    x: 1.3, y: 5.2, w: 9.6, h: 0.7, fontSize: 12, fontFace: "Segoe UI", color: ACCENT2, align: "center", valign: "middle",
  });

  // Layer stack on the right
  slide.addShape(pptx.ShapeType.roundRect, {
    x: 1.3, y: 6.1, w: 3.0, h: 0.55, fill: { color: "1A2332" }, line: { color: ACCENT2, width: 0.8 }, rectRadius: 0.04,
  });
  slide.addText("Local Layer (rw)", { x: 1.3, y: 6.1, w: 3.0, h: 0.55, fontSize: 11, fontFace: "Segoe UI Semibold", color: ACCENT2, align: "center", valign: "middle" });

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 4.6, y: 6.1, w: 3.0, h: 0.55, fill: { color: "1A2332" }, line: { color: ACCENT, width: 0.8 }, rectRadius: 0.04,
  });
  slide.addText("Shared Layers (ro)", { x: 4.6, y: 6.1, w: 3.0, h: 0.55, fontSize: 11, fontFace: "Segoe UI Semibold", color: ACCENT, align: "center", valign: "middle" });

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 7.9, y: 6.1, w: 3.0, h: 0.55, fill: { color: "1A2332" }, line: { color: ACCENT3, width: 0.8 }, rectRadius: 0.04,
  });
  slide.addText("Base Layers (ro)", { x: 7.9, y: 6.1, w: 3.0, h: 0.55, fontSize: 11, fontFace: "Segoe UI Semibold", color: ACCENT3, align: "center", valign: "middle" });
}

// ════════════════════════════════════════════════════════════
// SLIDE 5 — Memory Types
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Four Memory Types");
  accentBar(slide);
  subtitleText(slide, "Each type has distinct creation patterns, lifecycles, and retrieval characteristics");

  const types = [
    {
      icon: "\u{1F4DD}", name: "Observation", desc: "Raw facts/events from a session",
      example: '"User prefers tabs over spaces in this repo"',
      lifecycle: "Short-lived (30d) \u2192 consolidates into Insights",
      color: ACCENT,
    },
    {
      icon: "\u{1F4A1}", name: "Insight", desc: "Stable knowledge derived from observations",
      example: '"This repo uses 4-space indentation, EditorConfig enforced"',
      lifecycle: "Long-lived (90d), updated via validity intervals",
      color: ACCENT2,
    },
    {
      icon: "\u{1F4CB}", name: "Procedure", desc: "Reusable workflows and patterns",
      example: '"To deploy: run dotnet publish, then copy to server"',
      lifecycle: "Long-lived (365d), versioned, sub-linear decay",
      color: ACCENT3,
    },
    {
      icon: "\u26A1", name: "Heuristic", desc: "Do/don't lessons from experience",
      example: '"Never run migrations before backup \u2014 learned the hard way"',
      lifecycle: "Nearly immortal (730d half-life)",
      color: ACCENT4,
    },
  ];

  types.forEach((t, i) => {
    const bx = 0.8 + (i % 2) * 6.0;
    const by = 1.7 + Math.floor(i / 2) * 2.7;

    slide.addShape(pptx.ShapeType.roundRect, {
      x: bx, y: by, w: 5.7, h: 2.4, fill: { color: DARK2 }, line: { color: t.color, width: 1.5 }, rectRadius: 0.08,
    });

    slide.addText(`${t.icon}  ${t.name}`, {
      x: bx + 0.2, y: by + 0.1, w: 5.3, h: 0.4,
      fontSize: 18, fontFace: "Segoe UI Semibold", color: t.color, bold: true,
    });

    slide.addText(t.desc, {
      x: bx + 0.2, y: by + 0.5, w: 5.3, h: 0.35,
      fontSize: 13, fontFace: "Segoe UI", color: TEXT,
    });

    slide.addText(`Example: ${t.example}`, {
      x: bx + 0.2, y: by + 0.9, w: 5.3, h: 0.55,
      fontSize: 12, fontFace: "Segoe UI", color: TEXT_DIM, italic: true, lineSpacingMultiple: 1.2,
    });

    slide.addText(t.lifecycle, {
      x: bx + 0.2, y: by + 1.6, w: 5.3, h: 0.35,
      fontSize: 12, fontFace: "Segoe UI Semibold", color: t.color,
    });
  });

  footerNote(slide, "Typed stores + per-type budgets beat a single bucket by 30+ percentage points (ENGRAM ablation studies)");
}

// ════════════════════════════════════════════════════════════
// SLIDE 6 — Memory Layers (Docker Analogy)
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Memory Layers");
  accentBar(slide);
  subtitleText(slide, 'Docker-inspired layered knowledge \u2014 "eidet pack" for project intelligence');

  const stackX = 1.5;
  const stackW = 5.5;
  const layers = [
    { label: "Local Layer (read-write)", desc: "Your observations, insights, procedures", example: '"The API timeout was changed to 30s"', color: ACCENT2 },
    { label: "Shared Layer: team-backend", desc: "Team conventions (read-only)", example: '"We use FluentValidation for all DTOs"', color: ACCENT },
    { label: "Base Layer: vidyano-v6", desc: "Framework author knowledge (read-only)", example: '"Use AddVidyanoRavenDB() in Startup"', color: ACCENT3 },
    { label: "Base Layer: acme-utils-v3", desc: "Library author knowledge (read-only)", example: '"IndexHelper.Register() auto-discovers indexes"', color: ACCENT4 },
  ];

  layers.forEach((l, i) => {
    const ly = 1.8 + i * 1.25;
    slide.addShape(pptx.ShapeType.roundRect, {
      x: stackX, y: ly, w: stackW, h: 1.05, fill: { color: DARK2 }, line: { color: l.color, width: 1.5 }, rectRadius: 0.06,
    });
    slide.addText([
      { text: l.label, options: { fontSize: 13, fontFace: "Segoe UI Semibold", color: l.color, breakType: "none" } },
      { text: `  \u2014  ${l.desc}`, options: { fontSize: 12, fontFace: "Segoe UI", color: TEXT_DIM } },
      { text: `\n${l.example}`, options: { fontSize: 11, fontFace: "Segoe UI", color: TEXT, italic: true } },
    ], {
      x: stackX + 0.2, y: ly, w: stackW - 0.4, h: 1.05, valign: "middle", lineSpacingMultiple: 1.25,
    });
  });

  const rightX = 7.5;
  slide.addText("Key Properties", {
    x: rightX, y: 1.8, w: 5.0, h: 0.4,
    fontSize: 16, fontFace: "Segoe UI Semibold", color: WHITE,
  });

  const props = bulletList([
    "Writes always go to Local layer",
    "Base/Shared are immutable",
    "Recall searches ALL layers",
    "Results tagged with layer source",
    "New pack version = re-import",
    "Your local memories stay untouched",
    ".eidet packs are portable",
    "Auto-mount via dependency detection",
  ], { fontSize: 13 });

  slide.addText(props, { x: rightX, y: 2.3, w: 5.0, h: 4.5, valign: "top" });
}

// ════════════════════════════════════════════════════════════
// SLIDE 7 — Tiered Loading
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Tiered Context Loading");
  accentBar(slide);
  subtitleText(slide, "Minimal wake-up cost: < 600 tokens total at session start");

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 0.8, y: 1.8, w: 3.6, h: 3.0, fill: { color: DARK2 }, line: { color: ACCENT, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("L0 \u2014 Identity", {
    x: 0.8, y: 1.85, w: 3.6, h: 0.45, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT, align: "center",
  });
  slide.addText("~50 tokens", {
    x: 0.8, y: 2.25, w: 3.6, h: 0.3, fontSize: 13, fontFace: "Segoe UI", color: TEXT_DIM, align: "center",
  });
  slide.addText(
    "Repo: MyProject\nStack: .NET 10, WPF, RavenDB\nLast session: 2h ago\nMemories: 47 obs, 12 ins, 3 proc\nLayers: acme-utils-v3 (82)\nLinks: depends-on acme-utils",
    {
      x: 1.0, y: 2.7, w: 3.2, h: 2.0,
      fontSize: 11, fontFace: "Cascadia Code", color: TEXT, fill: { color: "0D1117" },
      margin: [6, 8, 6, 8], valign: "top", lineSpacingMultiple: 1.3,
    }
  );

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 4.8, y: 1.8, w: 4.0, h: 3.0, fill: { color: DARK2 }, line: { color: ACCENT2, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("L1 \u2014 Top-K Relevant", {
    x: 4.8, y: 1.85, w: 4.0, h: 0.45, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT2, align: "center",
  });
  slide.addText("~500 tokens  \u00B7  20 items (dense packing)", {
    x: 4.8, y: 2.25, w: 4.0, h: 0.3, fontSize: 12, fontFace: "Segoe UI", color: TEXT_DIM, align: "center",
  });
  slide.addText(
    "[I] 4-space indentation, EditorConfig\n[I] Terse responses, no summaries\n[P] Deploy: dotnet publish\n[I] MainViewModel = palette hub\n[H] Always update SHORTCUTS.md\n[I] [acme] IndexHelper auto-discovers\n[P] [vidyano] AddVidyanoRavenDB()\n...",
    {
      x: 5.0, y: 2.7, w: 3.6, h: 2.0,
      fontSize: 11, fontFace: "Cascadia Code", color: TEXT, fill: { color: "0D1117" },
      margin: [6, 8, 6, 8], valign: "top", lineSpacingMultiple: 1.3,
    }
  );

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 9.2, y: 1.8, w: 3.5, h: 3.0, fill: { color: DARK2 }, line: { color: ACCENT3, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("L2 \u2014 On-Demand", {
    x: 9.2, y: 1.85, w: 3.5, h: 0.45, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT3, align: "center",
  });
  slide.addText("Unbounded \u00B7 Full hybrid search", {
    x: 9.2, y: 2.25, w: 3.5, h: 0.3, fontSize: 12, fontFace: "Segoe UI", color: TEXT_DIM, align: "center",
  });
  slide.addText(
    "Triggered by explicit\neidet_recall calls\n\nVector similarity +\nfull-text + metadata\nfilters\n\nCross-repo support\nwith layer awareness",
    {
      x: 9.4, y: 2.7, w: 3.1, h: 2.0,
      fontSize: 12, fontFace: "Segoe UI", color: TEXT,
      valign: "top", lineSpacingMultiple: 1.35,
    }
  );

  infoBox(slide, "L1 Scoring:  score = importance \u00D7 0.3 + confidence \u00D7 0.15 + recency \u00D7 0.25 + frequency \u00D7 0.3\nBudgets: Insights 50% \u00B7 Procedures 30% \u00B7 Heuristics 20%  \u2014  Uses OneLiner > Summary > Content hierarchy", {
    y: 5.2, h: 1.1,
  });

  footerNote(slide, "Inspired by MemPalace's tiered approach \u2014 simplified for minimal context overhead");
}

// ════════════════════════════════════════════════════════════
// SLIDE 8 — Cross-Repo Linking
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Cross-Repo Knowledge Flow");
  accentBar(slide);
  subtitleText(slide, "Libraries, frameworks, and related projects share knowledge via explicit links");

  const repos = [
    { name: "MyApp", x: 5.0, y: 2.0, color: ACCENT },
    { name: "Acme.Utilities", x: 1.5, y: 4.0, color: ACCENT4 },
    { name: "Vidyano.Service", x: 8.5, y: 4.0, color: ACCENT3 },
  ];

  repos.forEach((r) => {
    slide.addShape(pptx.ShapeType.roundRect, {
      x: r.x, y: r.y, w: 3.0, h: 0.7, fill: { color: DARK2 }, line: { color: r.color, width: 1.5 }, rectRadius: 0.08,
    });
    slide.addText(r.name, {
      x: r.x, y: r.y, w: 3.0, h: 0.7, fontSize: 15, fontFace: "Segoe UI Semibold", color: r.color, align: "center", valign: "middle",
    });
  });

  slide.addText("depends-on", {
    x: 2.5, y: 3.0, w: 3.0, h: 0.35, fontSize: 12, fontFace: "Segoe UI", color: TEXT_DIM, align: "center",
  });
  slide.addText("depends-on", {
    x: 7.5, y: 3.0, w: 3.0, h: 0.35, fontSize: 12, fontFace: "Segoe UI", color: TEXT_DIM, align: "center",
  });

  slide.addText("Automatic Dependency Detection", {
    x: 0.8, y: 5.0, w: 5.5, h: 0.4, fontSize: 16, fontFace: "Segoe UI Semibold", color: WHITE,
  });

  const autoItems = bulletList([
    "NuGet PackageReference \u2192 depends-on links",
    "npm package.json \u2192 depends-on links",
    "Git submodules \u2192 depends-on links",
    "ProducedPackageId auto-detection (.csproj)",
    "Sibling project layers auto-mounted",
  ], { fontSize: 13 });
  slide.addText(autoItems, { x: 0.8, y: 5.4, w: 5.5, h: 2.0, valign: "top" });

  slide.addText("Cross-Repo Recall", {
    x: 7.0, y: 5.0, w: 5.5, h: 0.4, fontSize: 16, fontFace: "Segoe UI Semibold", color: WHITE,
  });

  const recallItems = bulletList([
    "Collect linked repos from current repo",
    "Include mounted base/shared layers",
    "Execute hybrid search across all scopes",
    "De-boost cross-repo results by 0.8\u00D7",
    "Tag results with source repo/layer",
  ], { fontSize: 13 });
  slide.addText(recallItems, { x: 7.0, y: 5.4, w: 5.5, h: 2.0, valign: "top" });
}

// ════════════════════════════════════════════════════════════
// SLIDE 9 — Intake & Packs
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Intake System & Eidet Packs");
  accentBar(slide);
  subtitleText(slide, "Immediate value from first session \u2014 no cold start");

  slide.addText("Intake Sources", {
    x: 0.8, y: 1.8, w: 5.5, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT,
  });

  const sources = [
    ["CLAUDE.md", "Existing memory entries \u2192 insights", "High"],
    ["README.md", "Project description, setup \u2192 insights + procedures", "Medium"],
    ["*.csproj / package.json", "Dependencies \u2192 cross-repo links", "Medium"],
    ["~/.claude/projects/*/memory/", "Claude Code auto-memory files", "High"],
    [".memory-intake.json", "Explicit structured memory seeds", "High"],
    [".eidet packs", "Pre-packaged knowledge \u2192 base layer", "High"],
  ];

  slide.addTable(
    [
      [
        { text: "Source", options: { fontSize: 12, bold: true, color: ACCENT, fill: { color: DARK2 } } },
        { text: "Extracted As", options: { fontSize: 12, bold: true, color: ACCENT, fill: { color: DARK2 } } },
        { text: "Priority", options: { fontSize: 12, bold: true, color: ACCENT, fill: { color: DARK2 } } },
      ],
      ...sources.map((s) => [
        { text: s[0], options: { fontSize: 11, color: TEXT, fill: { color: DARK } } },
        { text: s[1], options: { fontSize: 11, color: TEXT, fill: { color: DARK } } },
        { text: s[2], options: { fontSize: 11, color: s[2] === "High" ? ACCENT2 : ACCENT4, fill: { color: DARK } } },
      ]),
    ],
    {
      x: 0.8, y: 2.3, w: 5.8,
      border: { type: "solid", pt: 0.5, color: "30363D" },
      fontFace: "Segoe UI",
      margin: [4, 6, 4, 6],
    }
  );

  slide.addText("Eidet Packs (.eidet)", {
    x: 7.2, y: 1.8, w: 5.5, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT3,
  });

  const packItems = bulletList([
    { text: "Exportable knowledge packages", bold: true, color: WHITE },
    { text: 'Like Docker images for project knowledge \u2014 curate, export, share', indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 4 },
    { text: "Author Workflow", bold: true, color: ACCENT3, bulletColor: ACCENT3 },
    { text: "Build knowledge \u2192 eidet pack export \u2192 share via git/email/network", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 4 },
    { text: "Consumer Workflow", bold: true, color: ACCENT3, bulletColor: ACCENT3 },
    { text: "Place in pack directory \u2192 auto-mount by dependency match \u2192 ready", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 4 },
    { text: "Auto-Mount", bold: true, color: ACCENT3, bulletColor: ACCENT3 },
    { text: "Detects NuGet/npm refs \u2192 matches ApplicablePackages \u2192 mounts as read-only layer", indentLevel: 1 },
    { text: "", bullet: false, paraSpaceAfter: 4 },
    { text: "Git-Backed Registry", bold: true, color: ACCENT3, bulletColor: ACCENT3 },
    { text: "Pack directory can be a git repo with auto-pull on startup", indentLevel: 1 },
  ], { fontSize: 13 });

  slide.addText(packItems, { x: 7.2, y: 2.3, w: 5.5, h: 4.8, valign: "top" });
}

// ════════════════════════════════════════════════════════════
// SLIDE 10 — MCP Tools
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "13 MCP Tools");
  accentBar(slide);
  subtitleText(slide, "Full lifecycle management via the Model Context Protocol");

  const tools = [
    { name: "eidet_store", desc: "Store observations, insights, procedures, heuristics", group: "Core" },
    { name: "eidet_recall", desc: "Hybrid search (vector + full-text + metadata)", group: "Core" },
    { name: "eidet_context", desc: "L0 + L1 context block for session start", group: "Core" },
    { name: "eidet_forget", desc: "Soft-delete with reason tracking", group: "Core" },
    { name: "eidet_intake", desc: "Ingest CLAUDE.md, README, deps as seeds", group: "Intake" },
    { name: "eidet_link", desc: "Cross-repo and memory-to-memory relationships", group: "Linking" },
    { name: "eidet_consolidate", desc: "Merge observations into insights", group: "Lifecycle" },
    { name: "eidet_history", desc: "Version chain for a memory (supersession)", group: "Lifecycle" },
    { name: "eidet_feedback", desc: "Echo/fizzle feedback loop for recall quality", group: "Lifecycle" },
    { name: "eidet_maintenance", desc: "Dedup, decay, TTL expiry, orphan cleanup", group: "Lifecycle" },
    { name: "eidet_export", desc: "Export memories as formatted markdown", group: "Sharing" },
    { name: "eidet_pack_export", desc: "Export as shareable .eidet pack", group: "Sharing" },
    { name: "eidet_pack_import", desc: "Import .eidet pack as read-only layer", group: "Sharing" },
  ];

  const rows = [
    [
      { text: "Tool", options: { fontSize: 12, bold: true, color: ACCENT, fill: { color: DARK2 }, fontFace: "Segoe UI Semibold" } },
      { text: "Description", options: { fontSize: 12, bold: true, color: ACCENT, fill: { color: DARK2 }, fontFace: "Segoe UI Semibold" } },
      { text: "Group", options: { fontSize: 12, bold: true, color: ACCENT, fill: { color: DARK2 }, fontFace: "Segoe UI Semibold" } },
    ],
    ...tools.map((t) => [
      { text: t.name, options: { fontSize: 10.5, color: ACCENT2, fill: { color: DARK }, fontFace: "Cascadia Code" } },
      { text: t.desc, options: { fontSize: 10.5, color: TEXT, fill: { color: DARK }, fontFace: "Segoe UI" } },
      { text: t.group, options: { fontSize: 10.5, color: TEXT_DIM, fill: { color: DARK }, fontFace: "Segoe UI" } },
    ]),
  ];

  slide.addTable(rows, {
    x: 0.8, y: 1.7, w: 11.7,
    border: { type: "solid", pt: 0.5, color: "30363D" },
    margin: [3, 6, 3, 6],
    autoPage: false,
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 11 — Write Gates & Security
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Write Gates & Security");
  accentBar(slide);
  subtitleText(slide, "Pre-storage gates prevent noise and secrets \u2014 cannot be disabled");

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 0.8, y: 1.8, w: 5.7, h: 3.5, fill: { color: DARK2 }, line: { color: ACCENT5, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("\u{1F512}  Secret Scanner", {
    x: 1.0, y: 1.85, w: 5.3, h: 0.45, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT5,
  });
  slide.addText("Rejects content matching 13 regex patterns:", {
    x: 1.0, y: 2.3, w: 5.3, h: 0.3, fontSize: 12, fontFace: "Segoe UI", color: TEXT_DIM,
  });

  const secrets = bulletList([
    "AWS access keys (AKIA...)",
    "API secret keys (sk-...)",
    "GitHub tokens (ghp_, gho_, github_pat_)",
    "Bearer tokens (40+ char)",
    "JWT tokens (eyJ...)",
    "Private keys (-----BEGIN PRIVATE KEY-----)",
    "Connection string passwords",
    "Secret environment variables",
    "Base64-encoded keys (40+ char)",
    "npm tokens (npm_...)",
    "Azure storage keys",
    "GCP service account keys",
    "Slack tokens (xoxb-, xoxp-)",
  ], { fontSize: 10.5 });
  slide.addText(secrets, { x: 1.0, y: 2.6, w: 5.3, h: 2.6, valign: "top" });

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 6.8, y: 1.8, w: 5.7, h: 3.5, fill: { color: DARK2 }, line: { color: ACCENT4, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("\u{1F3AF}  Signal Gate", {
    x: 7.0, y: 1.85, w: 5.3, h: 0.45, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT4,
  });
  slide.addText("Rejects low-signal content:", {
    x: 7.0, y: 2.3, w: 5.3, h: 0.3, fontSize: 12, fontFace: "Segoe UI", color: TEXT_DIM,
  });

  const signals = bulletList([
    'Empty or < 20 chars \u2014 too short',
    '"tests passed", "it works", "done"',
    '"I will...", "Let me...", "I\'m going to..."',
    "Agent self-talk and filler",
    "Near-duplicates (vector sim > 0.92)",
  ], { fontSize: 12 });
  slide.addText(signals, { x: 7.0, y: 2.6, w: 5.3, h: 2.0, valign: "top" });

  slide.addText("Privacy Guarantees", {
    x: 0.8, y: 5.6, w: 11.7, h: 0.4, fontSize: 16, fontFace: "Segoe UI Semibold", color: WHITE,
  });

  const secItems = bulletList([
    "Fully local \u2014 localhost-bound API, no data leaves the machine",
    "Secret scanner (13 patterns) cannot be disabled \u2014 runs on every store, no bypass",
    "Memory content is data, never treated as system instructions (no prompt injection)",
    "Provenance tracking \u2014 every memory records source, session ID; tainted memories can be bulk-invalidated",
  ], { fontSize: 13 });
  slide.addText(secItems, { x: 0.8, y: 6.0, w: 11.7, h: 1.4, valign: "top" });
}

// ════════════════════════════════════════════════════════════
// SLIDE 12 — Consolidation & Maintenance
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Consolidation & Maintenance");
  accentBar(slide);
  subtitleText(slide, "Observations \u2192 Insights, automatic cleanup, differential decay");

  slide.addText("Consolidation Pipeline", {
    x: 0.8, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT2,
  });

  const consItems = bulletList([
    { text: "Query recent valid observations", color: TEXT },
    { text: "Group by tag overlap (\u2265 1 shared tag)", color: TEXT },
    { text: "Groups with \u2265 3 observations \u2192 candidates", color: TEXT },
    { text: "Check existing insights for topic coverage", color: TEXT },
    { text: "Create new insight (or boost existing)", color: TEXT },
    { text: "LLM-assisted merge for large groups (>5 obs)", color: TEXT_DIM },
    { text: "Uses Ollama locally when available", color: TEXT_DIM },
  ], { fontSize: 13 });
  slide.addText(consItems, { x: 0.8, y: 2.3, w: 5.7, h: 3.0, valign: "top" });

  slide.addText("Maintenance Pipeline (every 24h)", {
    x: 7.0, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT4,
  });

  const stages = [
    ["Stage 1", "TTL Expiry", "Expire memories past ForgetAfter date"],
    ["Stage 2", "Observation Retention", "Auto-expire old observations"],
    ["Stage 3", "Dedup Sweep", "Jaccard similarity > 0.85 \u2192 merge"],
    ["Stage 4", "Importance Decay", "FadeMem differential curves"],
    ["Stage 5", "Orphan Cleanup", "Remove empty/low-signal entries"],
    ["Stage 6", "Backfill Enrichment", "Entities, one-liners, foresight"],
  ];

  stages.forEach((s, i) => {
    const sy = 2.35 + i * 0.55;
    slide.addText(s[0], {
      x: 7.0, y: sy, w: 0.9, h: 0.4, fontSize: 11, fontFace: "Cascadia Code", color: ACCENT4,
    });
    slide.addText(s[1], {
      x: 7.9, y: sy, w: 2.3, h: 0.4, fontSize: 12, fontFace: "Segoe UI Semibold", color: TEXT,
    });
    slide.addText(s[2], {
      x: 10.2, y: sy, w: 2.5, h: 0.4, fontSize: 11, fontFace: "Segoe UI", color: TEXT_DIM,
    });
  });

  infoBox(slide, "FadeMem Differential Decay:  Observations 30d super-linear (shape 1.2)  \u00B7  Insights 90d linear  \u00B7  Procedures 365d sub-linear (0.8)  \u00B7  Heuristics 730d sub-linear (0.7)\nActivity-day aware: dormant repos skip decay. High-confidence memories resist decay (up to 1.25\u00D7 half-life).", {
    y: 5.8, h: 1.1,
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 13 — Design Decisions
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Design Decisions & Best Practices");
  accentBar(slide);
  subtitleText(slide, "Research-backed choices \u2014 why this architecture works");

  const decisions = [
    {
      q: "Why RavenDB (not SQLite + ChromaDB)?",
      a: "Single process, single index for vector + FTS + metadata. Built-in embeddings. One round-trip hybrid search. Dual mode: external or embedded.",
      color: ACCENT,
    },
    {
      q: "Why typed memories (not a single bucket)?",
      a: "ENGRAM ablation: typed stores + per-type budgets beat one bucket by 30+ pp. Enables different retention, retrieval, and consolidation per type.",
      color: ACCENT2,
    },
    {
      q: "Why append-only with validity intervals?",
      a: "Zep/Hindsight research: preserves audit trail, trivially syncable (CRDT-like), no accidental data loss. Enables future team collaboration.",
      color: ACCENT3,
    },
    {
      q: "Why zero-LLM write path?",
      a: "Deterministic, fast, free, testable. No API latency or model drift. LLM only used optionally for consolidation merge (Ollama, background-only).",
      color: ACCENT4,
    },
    {
      q: "Why a standalone service?",
      a: "Universal: any MCP client gets memory. Always running: Windows Service / launchd / systemd. One database serves all projects, all tools.",
      color: ACCENT5,
    },
    {
      q: "Why an intake system?",
      a: "Immediate value from session one. Bridges MEMORY.md \u2192 semantic search. Idempotent re-runs. Structured seeding > gradual rediscovery.",
      color: ACCENT,
    },
  ];

  decisions.forEach((d, i) => {
    const dy = 1.75 + i * 0.9;
    slide.addText(d.q, {
      x: 0.8, y: dy, w: 5.0, h: 0.4,
      fontSize: 14, fontFace: "Segoe UI Semibold", color: d.color,
    });
    slide.addText(d.a, {
      x: 5.8, y: dy, w: 6.7, h: 0.7,
      fontSize: 12.5, fontFace: "Segoe UI", color: TEXT, valign: "top", lineSpacingMultiple: 1.2,
    });
    if (i < decisions.length - 1) {
      slide.addShape(pptx.ShapeType.rect, {
        x: 0.8, y: dy + 0.8, w: 11.7, h: 0.01, fill: { color: "30363D" },
      });
    }
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 14 — SOTA Research
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Research-Informed Enhancements");
  accentBar(slide);
  subtitleText(slide, "Standing on the shoulders of 50+ systems and 40+ papers (ICLR 2026, NeurIPS 2025)");

  const research = [
    { feature: "Typed Memory + Budgets", source: "ENGRAM", desc: "+30pp on LoCoMo benchmark" },
    { feature: "Differential Decay (FadeMem)", source: "FadeMem", desc: "45% storage reduction" },
    { feature: "Version Chains", source: "Supermemory", desc: "Full supersession audit trail" },
    { feature: "Echo/Fizzle Feedback", source: "@jumperz / Codex", desc: "Closes recall quality loop" },
    { feature: "Auto-Link (Zettelkasten)", source: "A-MEM (NeurIPS '25)", desc: "Automatic knowledge graph" },
    { feature: "Heuristic Memory Type", source: "ERL (ICLR '26)", desc: "Do/don't lessons, near-immortal" },
    { feature: "Entity Extraction", source: "Cognee / Neo4j", desc: "9 regex patterns, zero-LLM" },
    { feature: "Foresight Hints", source: "EverMemOS", desc: "Predictive relevance signal" },
    { feature: "Provenance Tracking", source: "Mem0", desc: "User vs agent vs tool origin" },
    { feature: "Activity-Day Decay", source: "MIRA-OSS", desc: "Fair decay for dormant repos" },
    { feature: "Secret Scanning Gate", source: "Gigabrain / Codex", desc: "13 regex patterns block secrets" },
    { feature: "Confidence Scoring", source: "@jumperz", desc: "Separate from importance" },
  ];

  const rows = [
    [
      { text: "Enhancement", options: { fontSize: 11, bold: true, color: ACCENT, fill: { color: DARK2 }, fontFace: "Segoe UI Semibold" } },
      { text: "Source", options: { fontSize: 11, bold: true, color: ACCENT, fill: { color: DARK2 }, fontFace: "Segoe UI Semibold" } },
      { text: "Impact", options: { fontSize: 11, bold: true, color: ACCENT, fill: { color: DARK2 }, fontFace: "Segoe UI Semibold" } },
    ],
    ...research.map((r) => [
      { text: r.feature, options: { fontSize: 10.5, color: WHITE, fill: { color: DARK }, fontFace: "Segoe UI Semibold" } },
      { text: r.source, options: { fontSize: 10.5, color: ACCENT3, fill: { color: DARK }, fontFace: "Segoe UI" } },
      { text: r.desc, options: { fontSize: 10.5, color: TEXT_DIM, fill: { color: DARK }, fontFace: "Segoe UI" } },
    ]),
  ];

  slide.addTable(rows, {
    x: 0.8, y: 1.7, w: 11.7,
    border: { type: "solid", pt: 0.5, color: "30363D" },
    margin: [3, 6, 3, 6],
    autoPage: false,
  });

  footerNote(slide, "272+ tests passing \u00B7 .NET 10 \u00B7 Zero warnings");
}

// ════════════════════════════════════════════════════════════
// SLIDE 15 — Ollama Integration
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Optional Ollama Integration");
  accentBar(slide);
  subtitleText(slide, "Local LLM enrichment \u2014 opt-in, privacy-preserving, background-only");

  slide.addText("6 Enrichment Tasks", {
    x: 0.8, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT2,
  });

  const tasks = [
    { name: "One-Liners", desc: "Ultra-compact ~10 word summary for dense L1 packing" },
    { name: "Summaries", desc: "1-2 sentence summary for medium-context display" },
    { name: "Foresight Hints", desc: "Predict when/how memory will be useful" },
    { name: "Entity Extraction", desc: "LLM-assisted extraction (supplements regex)" },
    { name: "Consolidation Merge", desc: "Merge >5 observations into a coherent insight" },
    { name: "Conflict Detection", desc: "Flag contradictions with existing knowledge" },
  ];

  tasks.forEach((t, i) => {
    const ty = 2.3 + i * 0.65;
    slide.addText(t.name, {
      x: 0.8, y: ty, w: 2.0, h: 0.5,
      fontSize: 13, fontFace: "Segoe UI Semibold", color: ACCENT2,
    });
    slide.addText(t.desc, {
      x: 2.8, y: ty, w: 3.7, h: 0.5,
      fontSize: 12, fontFace: "Segoe UI", color: TEXT,
    });
  });

  slide.addText("Implementation Details", {
    x: 7.0, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT4,
  });

  const details = bulletList([
    "Uses /api/chat endpoint (not /api/generate)",
    "think: false for thinking-model compatibility",
    "120s HttpClient timeout for cold starts",
    "Lazy health re-check (no startup blocking)",
    "Fire-and-forget for conflict detection",
    "Configurable URL, model, per-task toggles",
    "Default model: gemma4",
    "NullEnricher when disabled (zero overhead)",
    "Background-only \u2014 never blocks store/recall",
  ], { fontSize: 12.5 });
  slide.addText(details, { x: 7.0, y: 2.3, w: 5.7, h: 4.0, valign: "top" });

  infoBox(slide, "Key principle: Ollama enrichment is always additive and asynchronous. The core memory system works perfectly without it. It just makes memories richer over time.", {
    y: 6.0, h: 0.9,
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 16 — How It Works in Practice
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "How It Works in Practice");
  accentBar(slide);
  subtitleText(slide, "A typical session with Eidet");

  const steps = [
    { step: "1", label: "Session Start", desc: "Claude Code opens \u2192 eidet_context called \u2192 L0 identity + L1 top-20 memories injected (< 600 tokens)", color: ACCENT },
    { step: "2", label: "Working", desc: "AI discovers a bug root cause \u2192 eidet_store(type=observation, content='...', tags=['bug', 'auth'])", color: ACCENT2 },
    { step: "3", label: "Recall", desc: 'AI needs context \u2192 eidet_recall(query="auth middleware timeout") \u2192 hybrid search returns ranked results from all layers', color: ACCENT3 },
    { step: "4", label: "Feedback", desc: "AI used a recalled memory \u2192 eidet_feedback(id=..., used=true) \u2192 importance +0.05, confidence +0.10", color: ACCENT4 },
    { step: "5", label: "Session End", desc: "Auto-consolidation: 5 auth-related observations \u2192 1 insight. Maintenance pipeline runs: decay, dedup, cleanup.", color: ACCENT5 },
    { step: "6", label: "Next Session", desc: "New session (any tool) \u2192 L1 now includes the consolidated insight. Knowledge persists and improves.", color: ACCENT },
  ];

  steps.forEach((s, i) => {
    const sy = 1.75 + i * 0.9;

    slide.addShape(pptx.ShapeType.ellipse, {
      x: 0.8, y: sy + 0.05, w: 0.5, h: 0.5, fill: { color: s.color },
    });
    slide.addText(s.step, {
      x: 0.8, y: sy + 0.05, w: 0.5, h: 0.5, fontSize: 16, fontFace: "Segoe UI Semibold", color: DARK, align: "center", valign: "middle",
    });

    slide.addText(s.label, {
      x: 1.5, y: sy, w: 2.0, h: 0.55, fontSize: 14, fontFace: "Segoe UI Semibold", color: s.color, valign: "middle",
    });

    slide.addText(s.desc, {
      x: 3.5, y: sy, w: 9.0, h: 0.65, fontSize: 12.5, fontFace: "Segoe UI", color: TEXT, valign: "middle", lineSpacingMultiple: 1.2,
    });
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 17 — Echo / Fizzle & Confidence
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Feedback Loop & Confidence");
  accentBar(slide);
  subtitleText(slide, "Self-improving recall quality through echo/fizzle signals");

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 0.8, y: 1.8, w: 5.7, h: 2.3, fill: { color: DARK2 }, line: { color: ACCENT2, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("\u2713  Echo \u2014 Memory Was Useful", {
    x: 1.0, y: 1.85, w: 5.3, h: 0.45, fontSize: 16, fontFace: "Segoe UI Semibold", color: ACCENT2,
  });
  slide.addText([
    { text: "Importance: ", options: { fontSize: 13, color: TEXT } },
    { text: "+0.05", options: { fontSize: 13, color: ACCENT2, bold: true } },
    { text: "\nConfidence: ", options: { fontSize: 13, color: TEXT } },
    { text: "+0.10", options: { fontSize: 13, color: ACCENT2, bold: true } },
    { text: "\n\nMemory rises in L1 rankings. Decays slower.\nFuture recalls prioritize this knowledge.", options: { fontSize: 12, color: TEXT_DIM } },
  ], {
    x: 1.0, y: 2.35, w: 5.3, h: 1.6, valign: "top", lineSpacingMultiple: 1.3,
  });

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 6.8, y: 1.8, w: 5.7, h: 2.3, fill: { color: DARK2 }, line: { color: ACCENT5, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("\u2717  Fizzle \u2014 Memory Was Irrelevant", {
    x: 7.0, y: 1.85, w: 5.3, h: 0.45, fontSize: 16, fontFace: "Segoe UI Semibold", color: ACCENT5,
  });
  slide.addText([
    { text: "Importance: ", options: { fontSize: 13, color: TEXT } },
    { text: "\u22120.10", options: { fontSize: 13, color: ACCENT5, bold: true } },
    { text: "\nConfidence: ", options: { fontSize: 13, color: TEXT } },
    { text: "\u22120.15", options: { fontSize: 13, color: ACCENT5, bold: true } },
    { text: "\n\nMemory drops in rankings. Decays faster.\nEventually pruned by maintenance pipeline.", options: { fontSize: 12, color: TEXT_DIM } },
  ], {
    x: 7.0, y: 2.35, w: 5.3, h: 1.6, valign: "top", lineSpacingMultiple: 1.3,
  });

  slide.addText("Importance vs. Confidence \u2014 Two Independent Dimensions", {
    x: 0.8, y: 4.4, w: 11.7, h: 0.4, fontSize: 16, fontFace: "Segoe UI Semibold", color: WHITE,
  });

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 0.8, y: 4.9, w: 5.7, h: 1.0, fill: { color: "1A2332" }, line: { color: ACCENT, width: 1 }, rectRadius: 0.06,
  });
  slide.addText([
    { text: "Importance", options: { fontSize: 14, bold: true, color: ACCENT } },
    { text: " = How useful is this memory?\nSet by author. Decayed over time. Boosted by echo.", options: { fontSize: 12, color: TEXT } },
  ], { x: 1.0, y: 4.9, w: 5.3, h: 1.0, valign: "middle", lineSpacingMultiple: 1.3 });

  slide.addShape(pptx.ShapeType.roundRect, {
    x: 6.8, y: 4.9, w: 5.7, h: 1.0, fill: { color: "1A2332" }, line: { color: ACCENT3, width: 1 }, rectRadius: 0.06,
  });
  slide.addText([
    { text: "Confidence", options: { fontSize: 14, bold: true, color: ACCENT3 } },
    { text: " = How certain are we this is correct?\nUpdated by feedback. Affects decay resistance (up to 1.25\u00D7).", options: { fontSize: 12, color: TEXT } },
  ], { x: 7.0, y: 4.9, w: 5.3, h: 1.0, valign: "middle", lineSpacingMultiple: 1.3 });

  infoBox(slide, "L1 Scoring:  score = importance \u00D7 0.3 + confidence \u00D7 0.15 + recency \u00D7 0.25 + frequency \u00D7 0.3", {
    y: 6.2, h: 0.7,
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 18 — Rich TUI
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Rich TUI & Developer Experience");
  accentBar(slide);
  subtitleText(slide, "Built for developers AND AI agents \u2014 Spectre.Console powered");

  slide.addText("eidet setup", {
    x: 0.8, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Cascadia Code", color: ACCENT2,
  });

  const setupItems = bulletList([
    "Interactive wizard for first-time configuration",
    "Auto-detect RavenDB (external or embedded)",
    "Test connection, create database, deploy indexes",
    "Configure Ollama (optional)",
    "Generate MCP config for Claude Code / Cursor",
    "Verify everything works end-to-end",
  ], { fontSize: 13 });
  slide.addText(setupItems, { x: 0.8, y: 2.3, w: 5.7, h: 2.5, valign: "top" });

  slide.addText("eidet doctor", {
    x: 7.0, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Cascadia Code", color: ACCENT4,
  });

  const doctorItems = bulletList([
    "Health check: RavenDB, database, indexes, Ollama",
    "Suggested fix for each failure",
    "--json flag for AI agent consumption",
    "Exit code 0/1 for scripting",
  ], { fontSize: 13 });
  slide.addText(doctorItems, { x: 7.0, y: 2.3, w: 5.7, h: 2.0, valign: "top" });

  slide.addText("22 CLI Commands", {
    x: 0.8, y: 5.0, w: 11.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: WHITE,
  });

  const cliCols = [
    [
      "serve \u2014 Start REST API + MCP",
      "setup \u2014 Interactive wizard",
      "doctor \u2014 Health check + fixes",
      "status \u2014 Version & storage info",
      "install / uninstall \u2014 System service",
      "config get/set/list \u2014 All settings",
    ],
    [
      "store / recall / stats / export",
      "intake / maintain / quality",
      "api-key create/list/revoke",
      "backup create/restore/list/prune",
      "ollama status/pull/list",
      "instructions / docker / update",
    ],
  ];

  const leftItems = bulletList(cliCols[0], { fontSize: 12 });
  slide.addText(leftItems, { x: 0.8, y: 5.4, w: 5.7, h: 2.0, valign: "top" });

  const rightItems = bulletList(cliCols[1], { fontSize: 12 });
  slide.addText(rightItems, { x: 7.0, y: 5.4, w: 5.7, h: 2.0, valign: "top" });
}

// ════════════════════════════════════════════════════════════
// SLIDE 19 — Web UI & Knowledge Graph
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Web UI & Knowledge Graph");
  accentBar(slide);
  subtitleText(slide, "Built-in SPA at localhost:19380/ui \u2014 embedded in the binary, zero external files");

  // Left column - pages
  slide.addText("5 Pages", {
    x: 0.8, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT,
  });

  const pages = [
    { name: "Dashboard", desc: "Repo selector, memory counts by type, recent memories", color: ACCENT },
    { name: "Memory Browser", desc: "Full-text search, type filter, paginated results, detail panel", color: ACCENT2 },
    { name: "Knowledge Graph", desc: "Canvas-based force-directed graph, nodes colored by type, drag & hover", color: ACCENT3 },
    { name: "Timeline", desc: "Chronological view grouped by date, type badges, tag chips", color: ACCENT4 },
    { name: "Settings", desc: "Service status, action buttons: intake, consolidate, maintenance, export", color: ACCENT5 },
  ];

  pages.forEach((p, i) => {
    const py = 2.3 + i * 0.7;
    slide.addText(p.name, {
      x: 0.8, y: py, w: 2.0, h: 0.55, fontSize: 14, fontFace: "Segoe UI Semibold", color: p.color,
    });
    slide.addText(p.desc, {
      x: 2.8, y: py, w: 3.7, h: 0.55, fontSize: 12, fontFace: "Segoe UI", color: TEXT, valign: "middle",
    });
  });

  // Right column - implementation
  slide.addText("Implementation", {
    x: 7.0, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT4,
  });

  const implItems = bulletList([
    "Vanilla HTML/CSS/JS \u2014 no framework deps",
    "Dark theme, responsive layout",
    "Canvas-based force-directed graph engine",
    "Compiled as embedded resources in binary",
    "Served by EidetApiServer (HttpListener)",
    "MIME type mapping + path traversal protection",
    "Auth-exempt \u2014 no API key for /ui routes",
    "New API endpoints: /browse, /repos, /graph",
  ], { fontSize: 12.5 });
  slide.addText(implItems, { x: 7.0, y: 2.3, w: 5.7, h: 4.0, valign: "top" });

  infoBox(slide, "Graph nodes colored by type: blue=Observation, purple=Insight, green=Procedure, orange=Heuristic. Node size reflects importance. Edges from DerivedFrom links and explicit memory links.", {
    y: 6.0, h: 0.9,
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 20 — Hooks System
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Hooks System");
  accentBar(slide);
  subtitleText(slide, "Claude Code-inspired lifecycle hooks \u2014 gate or extend memory operations with custom code");

  // Left - hook events
  slide.addText("6 Lifecycle Events", {
    x: 0.8, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT2,
  });

  const events = [
    { name: "PreStore", desc: "Before storing \u2192 can reject (non-zero exit)", type: "Gate", color: ACCENT5 },
    { name: "PostStore", desc: "After storing \u2192 fire-and-forget", type: "Notify", color: ACCENT2 },
    { name: "PreRecall", desc: "Before recall \u2192 can block search", type: "Gate", color: ACCENT5 },
    { name: "PostRecall", desc: "After recall \u2192 log, transform, etc.", type: "Notify", color: ACCENT2 },
    { name: "PreForget", desc: "Before forget \u2192 can prevent deletion", type: "Gate", color: ACCENT5 },
    { name: "PostForget", desc: "After forget \u2192 audit trail, notifications", type: "Notify", color: ACCENT2 },
  ];

  events.forEach((e, i) => {
    const ey = 2.3 + i * 0.6;
    slide.addText(e.name, {
      x: 0.8, y: ey, w: 1.5, h: 0.5, fontSize: 13, fontFace: "Cascadia Code", color: e.color,
    });
    slide.addText(e.type, {
      x: 2.3, y: ey, w: 0.8, h: 0.5, fontSize: 11, fontFace: "Segoe UI Semibold", color: e.color,
    });
    slide.addText(e.desc, {
      x: 3.1, y: ey, w: 3.4, h: 0.5, fontSize: 12, fontFace: "Segoe UI", color: TEXT, valign: "middle",
    });
  });

  // Right - how it works
  slide.addText("How It Works", {
    x: 7.0, y: 1.8, w: 5.7, h: 0.4, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT4,
  });

  const hookItems = bulletList([
    "Hook = external command (any language)",
    "JSON context piped to stdin",
    "Pre-hooks: non-zero exit = reject operation",
    "Post-hooks: fire-and-forget, don't block",
    "Configurable timeout per hook (default 10s)",
    "Process tree kill on timeout",
    "Enable/disable per hook definition",
    "NullHookRunner: zero overhead when unconfigured",
  ], { fontSize: 12.5 });
  slide.addText(hookItems, { x: 7.0, y: 2.3, w: 5.7, h: 3.5, valign: "top" });

  codeBlock(slide, `# eidet.json hooks config
{
  "hooks": {
    "preStore": [
      { "command": "python validate.py", "timeoutSeconds": 5 }
    ],
    "postStore": [
      { "command": "curl -X POST http://slack/webhook" }
    ]
  }
}`, { y: 5.2, h: 2.2, fontSize: 11 });
}

// ════════════════════════════════════════════════════════════
// SLIDE 21 — Auth, Security & API Keys
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "API Key Auth & Network Security");
  accentBar(slide);
  subtitleText(slide, "Bearer token auth with scoped permissions \u2014 SHA256 hashed keys");

  // Left - scope model
  slide.addShape(pptx.ShapeType.roundRect, {
    x: 0.8, y: 1.8, w: 5.7, h: 3.2, fill: { color: DARK2 }, line: { color: ACCENT, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("4 Scope Levels", {
    x: 1.0, y: 1.85, w: 5.3, h: 0.45, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT,
  });

  const scopes = [
    { name: "read:all", desc: "Search, recall, browse, context, stats", color: ACCENT2 },
    { name: "write:observations", desc: "Store observations only (safe for agents)", color: ACCENT4 },
    { name: "write:all", desc: "Store any type, forget, feedback (implies write:observations)", color: ACCENT3 },
    { name: "admin", desc: "Maintenance, consolidation, backup, config (implies all)", color: ACCENT5 },
  ];

  scopes.forEach((s, i) => {
    const sy = 2.4 + i * 0.6;
    slide.addText(s.name, {
      x: 1.0, y: sy, w: 2.2, h: 0.5, fontSize: 13, fontFace: "Cascadia Code", color: s.color,
    });
    slide.addText(s.desc, {
      x: 3.2, y: sy, w: 3.1, h: 0.5, fontSize: 12, fontFace: "Segoe UI", color: TEXT, valign: "middle",
    });
  });

  // Right - security features
  slide.addShape(pptx.ShapeType.roundRect, {
    x: 6.8, y: 1.8, w: 5.7, h: 3.2, fill: { color: DARK2 }, line: { color: ACCENT4, width: 1.5 }, rectRadius: 0.08,
  });
  slide.addText("Security Features", {
    x: 7.0, y: 1.85, w: 5.3, h: 0.45, fontSize: 17, fontFace: "Segoe UI Semibold", color: ACCENT4,
  });

  const secFeatures = bulletList([
    "SHA256 hashed keys in config (no plaintext)",
    "Authorization: Bearer header on all requests",
    "Health/status always public (monitoring)",
    "Web UI routes auth-exempt (localhost)",
    "Network binding guard: non-localhost requires auth",
    "CORS headers for browser/Web UI access",
    "First key auto-enables auth",
    "Revoking last key auto-disables",
  ], { fontSize: 12 });
  slide.addText(secFeatures, { x: 7.0, y: 2.4, w: 5.3, h: 2.5, valign: "top" });

  codeBlock(slide, `# Create an API key with scopes
$ eidet api-key create "my-agent" --scopes read:all,write:observations
  Key: eidet_ak_7f3b2c1d...  (save this \u2014 shown only once)

# Use it
$ curl -H "Authorization: Bearer eidet_ak_7f3b2c1d..." \\
    http://localhost:19380/api/eidet/context?repo=MyApp`, { y: 5.3, h: 1.8, fontSize: 11 });
}

// ════════════════════════════════════════════════════════════
// SLIDE 22 — Production Readiness
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Production Readiness");
  accentBar(slide);
  subtitleText(slide, "CI/CD, Docker, quality analysis, backup/restore \u2014 ready for real use");

  const features = [
    {
      title: "CI/CD Pipeline",
      items: ["GitHub Actions: build + test on push/PR", "Matrix: Windows, Ubuntu, macOS", "Release workflow: self-contained binaries", "Auto-publish NuGet, npm, PyPI SDKs"],
      color: ACCENT,
    },
    {
      title: "Docker Support",
      items: ["Multi-stage Dockerfile (runtime-deps:10.0)", "docker-compose with optional RavenDB + Ollama", "Environment variable overrides for config", "Health check endpoint for orchestrators"],
      color: ACCENT2,
    },
    {
      title: "Quality Dashboard",
      items: ["8 automated checks (stale, fizzle, conflicts...)", "Overall score 0.0\u20131.0 with severity levels", "CLI: eidet quality --repo ... --json", "API: GET /api/eidet/quality"],
      color: ACCENT3,
    },
    {
      title: "Backup & Restore",
      items: ["RavenDB Smuggler API for full exports", ".eidetbackup ZIP with SHA256 checksum", "Retention policy + auto-prune", "CLI: eidet backup create/restore/list/prune"],
      color: ACCENT4,
    },
  ];

  features.forEach((f, i) => {
    const fx = (i % 2) * 6.3 + 0.8;
    const fy = 1.75 + Math.floor(i / 2) * 2.7;

    slide.addShape(pptx.ShapeType.roundRect, {
      x: fx, y: fy, w: 5.9, h: 2.4, fill: { color: DARK2 }, line: { color: f.color, width: 1.5 }, rectRadius: 0.08,
    });
    slide.addText(f.title, {
      x: fx + 0.2, y: fy + 0.1, w: 5.5, h: 0.4,
      fontSize: 16, fontFace: "Segoe UI Semibold", color: f.color,
    });

    const items = bulletList(f.items, { fontSize: 12 });
    slide.addText(items, { x: fx + 0.2, y: fy + 0.55, w: 5.5, h: 1.8, valign: "top" });
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 23 — Client SDKs
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "Client SDKs");
  accentBar(slide);
  subtitleText(slide, "TypeScript, Python, and C# SDKs for custom integrations");

  const sdks = [
    {
      name: "TypeScript", pkg: "@eidet/sdk (npm)", target: "ESM, zero runtime deps, native fetch",
      example: `import { EidetClient } from "@eidet/sdk";
const client = new EidetClient();
await client.store({
  repo: "my-app",
  content: "Always run migrations first",
  type: "heuristic"
});
const results = await client.recall("my-app", "migrations");`,
      color: ACCENT4,
    },
    {
      name: "Python", pkg: "eidet-sdk (pip)", target: "httpx, type hints, context manager, Python 3.10+",
      example: `from eidet_sdk import EidetClient
with EidetClient() as client:
    client.store(
        repo="my-app",
        content="Use pytest-xdist for parallel tests",
        type="heuristic"
    )
    results = client.recall("my-app", "testing")`,
      color: ACCENT2,
    },
    {
      name: "C#", pkg: "Eidet.Sdk (NuGet)", target: ".NET 8+, HttpClient, System.Text.Json, CancellationToken",
      example: `using var client = new EidetClient();
await client.StoreAsync(new StoreRequest {
    Repo = "my-app",
    Content = "Use FluentValidation for DTOs",
    Type = MemoryType.Insight
});
var results = await client.RecallAsync("my-app", "validation");`,
      color: ACCENT,
    },
  ];

  sdks.forEach((s, i) => {
    const sx = 0.8 + i * 4.1;

    slide.addShape(pptx.ShapeType.roundRect, {
      x: sx, y: 1.7, w: 3.8, h: 5.3, fill: { color: DARK2 }, line: { color: s.color, width: 1.5 }, rectRadius: 0.08,
    });

    slide.addText(s.name, {
      x: sx + 0.15, y: 1.75, w: 3.5, h: 0.4, fontSize: 18, fontFace: "Segoe UI Semibold", color: s.color,
    });

    slide.addText(s.pkg, {
      x: sx + 0.15, y: 2.15, w: 3.5, h: 0.3, fontSize: 11, fontFace: "Cascadia Code", color: TEXT_DIM,
    });

    slide.addText(s.target, {
      x: sx + 0.15, y: 2.45, w: 3.5, h: 0.4, fontSize: 11, fontFace: "Segoe UI", color: TEXT,
    });

    slide.addText(s.example, {
      x: sx + 0.15, y: 2.95, w: 3.5, h: 3.9, fontSize: 9.5, fontFace: "Cascadia Code", color: TEXT,
      fill: { color: "0D1117" }, margin: [6, 8, 6, 8], valign: "top", lineSpacingMultiple: 1.2,
    });
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 24 — Future
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);
  titleText(slide, "What the Future Brings");
  accentBar(slide);
  subtitleText(slide, "Designed for local-first today, team collaboration tomorrow");

  const futures = [
    {
      title: "Team Sync & Collaboration",
      desc: "Append-only events make sync trivial (no conflicts). Selective publishing \u2014 share insights/procedures, keep observations local.",
      color: ACCENT,
    },
    {
      title: "E2E Encryption (Bitwarden Model)",
      desc: "Personal key for private memories, team key for shared layers. Server never sees plaintext. Self-hosted or SaaS options.",
      color: ACCENT2,
    },
    {
      title: "Public Pack Registry",
      desc: "Community-maintained knowledge packs for popular frameworks. npm/NuGet for AI knowledge. eidet pack install react-patterns.",
      color: ACCENT3,
    },
    {
      title: "Active Learning & Prediction",
      desc: "Memory system that anticipates what you'll need \u2014 proactive context based on git state, file changes, time of day.",
      color: ACCENT4,
    },
    {
      title: "VS Code / JetBrains Extensions",
      desc: "Inline memory annotations, knowledge graph panel, memory browser sidebar, one-click store from editor context.",
      color: ACCENT5,
    },
    {
      title: "Multi-Model Enrichment",
      desc: "Support for additional local models beyond Ollama \u2014 llama.cpp, vLLM, MLX. Cloud enrichment opt-in for teams.",
      color: ACCENT,
    },
  ];

  futures.forEach((f, i) => {
    const fx = (i % 2) * 6.3 + 0.8;
    const fy = 1.75 + Math.floor(i / 2) * 1.8;

    slide.addShape(pptx.ShapeType.roundRect, {
      x: fx, y: fy, w: 5.9, h: 1.5, fill: { color: DARK2 }, line: { color: f.color, width: 1.2 }, rectRadius: 0.08,
    });
    slide.addText(f.title, {
      x: fx + 0.2, y: fy + 0.1, w: 5.5, h: 0.4,
      fontSize: 15, fontFace: "Segoe UI Semibold", color: f.color,
    });
    slide.addText(f.desc, {
      x: fx + 0.2, y: fy + 0.5, w: 5.5, h: 0.9,
      fontSize: 12, fontFace: "Segoe UI", color: TEXT, valign: "top", lineSpacingMultiple: 1.3,
    });
  });
}

// ════════════════════════════════════════════════════════════
// SLIDE 20 — Summary / Closing
// ════════════════════════════════════════════════════════════
{
  const slide = pptx.addSlide();
  addBackground(slide);

  slide.addShape(pptx.ShapeType.rect, {
    x: 0, y: 0, w: 13.33, h: 0.06, fill: { color: ACCENT },
  });

  slide.addText("Eidet", {
    x: 0.8, y: 1.0, w: 11.7, h: 0.8,
    fontSize: 44, fontFace: "Segoe UI Semibold", color: WHITE, bold: true,
  });

  slide.addText("Long-term memory for AI coding agents \u2014 local-first, privacy-absolute, works everywhere.", {
    x: 0.8, y: 1.8, w: 11.7, h: 0.5,
    fontSize: 18, fontFace: "Segoe UI", color: ACCENT,
  });

  const summary = [
    { text: "Universal", desc: " \u2014 Any MCP client: Claude Code, Cursor, Windsurf, Cline, custom tools" },
    { text: "4 Memory Types", desc: " \u2014 Observations, Insights, Procedures, Heuristics with distinct lifecycles" },
    { text: "Docker-Like Layers", desc: " \u2014 Local + Shared + Base with immutable base knowledge" },
    { text: "< 600 Token Wake-Up", desc: " \u2014 L0 identity + L1 top-K, dense packing with one-liners" },
    { text: "Hybrid Search", desc: " \u2014 Vector + full-text + metadata in a single RavenDB round-trip" },
    { text: "13 MCP Tools + REST API", desc: " \u2014 Full lifecycle: store, recall, forget, consolidate, export, share" },
    { text: "Hooks System", desc: " \u2014 Pre/post hooks for store, recall, forget \u2014 gate or extend via custom code" },
    { text: "Web UI + 3 SDKs", desc: " \u2014 Knowledge graph, memory browser, timeline + TypeScript, Python, C# SDKs" },
    { text: "Production Ready", desc: " \u2014 CI/CD, Docker, backup/restore, quality dashboard, API key auth, 272+ tests" },
  ];

  summary.forEach((s, i) => {
    const sy = 2.6 + i * 0.45;
    slide.addText([
      { text: s.text, options: { fontSize: 14, fontFace: "Segoe UI Semibold", color: ACCENT2, bold: true } },
      { text: s.desc, options: { fontSize: 13, fontFace: "Segoe UI", color: TEXT } },
    ], {
      x: 1.2, y: sy, w: 11.0, h: 0.42, valign: "middle",
    });
  });

  slide.addText("eidet.dev  \u00B7  .NET 10  \u00B7  RavenDB  \u00B7  MCP Protocol  \u00B7  272+ tests  \u00B7  22 CLI commands  \u00B7  3 SDKs", {
    x: 0.8, y: 6.5, w: 11.7, h: 0.4,
    fontSize: 14, fontFace: "Segoe UI", color: TEXT_DIM, align: "center",
  });
}

// ── Write ──
const outPath = "P:/Eidet/docs/Eidet-Presentation.pptx";
await pptx.writeFile({ fileName: outPath });
console.log(`Presentation saved to ${outPath}`);
