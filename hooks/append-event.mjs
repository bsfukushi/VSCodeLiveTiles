#!/usr/bin/env node
// VSCode Live Tiles hooks bridge: Claude Code Hooks -> events.jsonl
//
// Usage (from ~/.claude/settings.json hooks — install.mjs が自動登録する):
//   node /path/to/append-event.mjs <type>
//
// type:
//   post_tool_use | post_tool_use_failure | pre_tool_use | stop | user_prompt_submit |
//   notification | session_start | session_end | permission_request
//
// 出自: CCPet（クローズ済み 2026-07-10）の src/hooks/append-event.mjs を取り込み、
// VSCode Live Tiles 用に再設計したもの。変更点:
//   - 既定パスを ~/.vscode-live-tiles/events.jsonl に（env: VSCODE_LIVE_TILES_EVENTS_FILE）
//   - payload を丸ごと書くのをやめた。ウィジェットが読むのは background_tasks の
//     status だけなので、それ以外は死荷重（CCPet 時代は 1 行最大 635KB → 約 200B に）
//   - サイズ上限ローテーション（5MB / 1 世代）を追加。読み取り側は FileSystemWatcher の
//     Renamed/Created で開き直すので、リネームするだけで追従する
//
// stdin: Claude Code Hooks payload (JSON). At minimum we expect `session_id`.
//
// Output line (to events.jsonl):
//   {"ts":<ms epoch>,"type":"<type>","sessionId":"<id>","projectName":"<name|null>","cwd":"<cwd>"}
//   stop 等で background_tasks があるときのみ:
//   {...,"payload":{"background_tasks":[{"status":"running"},...]}}
//
// Design rules（CCPet SPEC §7 を踏襲）:
//   - 素の Node.js（依存ゼロ・ビルド不要）
//   - Claude Code 本体を絶対に止めないため、どんな失敗でも exit 0
//   - 1 行 = 1 イベント。途中で切れた行はパース側でスキップ前提

import {
  appendFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  renameSync,
  statSync,
  unlinkSync
} from 'node:fs'
import { homedir } from 'node:os'
import { basename, dirname, isAbsolute, join, resolve, sep } from 'node:path'

const VALID_TYPES = new Set([
  'post_tool_use',
  'post_tool_use_failure',
  'pre_tool_use',
  'stop',
  'user_prompt_submit',
  'notification',
  'session_start',
  'session_end',
  'permission_request'
])

/**
 * PreToolUse hook で events.jsonl に書く対象 tool 名。
 * AskUserQuestion 以外の PreToolUse は早期 return する（肥大化防止）。
 * settings.json の matcher で絞っている前提だが、matcher 漏れへの二段防御。
 */
const PRE_TOOL_USE_ALLOWED_TOOL_NAMES = new Set(['AskUserQuestion'])

const DEFAULT_EVENTS_FILE = join(homedir(), '.vscode-live-tiles', 'events.jsonl')

/** ローテーション上限。行が約 200B に痩せたので 5MB ≒ 2.5 万イベント ≒ 数週間分。 */
const MAX_FILE_BYTES = 5 * 1024 * 1024

/**
 * `VSCODE_LIVE_TILES_EVENTS_FILE` 経由の任意ファイル書き込みを防ぐ（CCPet review 2026-05-09 の踏襲）。
 * フック実行コンテキストは Claude Code が制御するため、悪意ある環境変数や
 * プロンプトインジェクション → hook 経由の任意ファイル書き込みに連鎖しうる。
 *   - isAbsolute / resolve() で正規化
 *   - realpathSync で symlink を実パスに展開してから homedir() 配下判定
 *   - それ以外は既定パスに強制フォールバック（stderr warn）
 *   - 検証失敗でも throw / exit !=0 はしない
 */
function resolveEventsFile() {
  const fromEnv = process.env.VSCODE_LIVE_TILES_EVENTS_FILE
  if (!fromEnv || fromEnv.trim().length === 0) return DEFAULT_EVENTS_FILE

  const candidate = resolve(fromEnv)
  if (!isAbsolute(candidate)) {
    process.stderr.write(`[append-event] VSCODE_LIVE_TILES_EVENTS_FILE rejected: not absolute. fallback to default.\n`)
    return DEFAULT_EVENTS_FILE
  }

  // symlink を実パスに展開してから homedir 配下判定
  let realCandidate
  try {
    realCandidate = realpathSync(candidate)
  } catch (err) {
    // ファイル未存在 (ENOENT) は新規作成シナリオなので、親ディレクトリの realpath で再判定
    if (err && err.code === 'ENOENT') {
      try {
        const realDir = realpathSync(dirname(candidate))
        realCandidate = join(realDir, basename(candidate))
      } catch {
        process.stderr.write(
          `[append-event] VSCODE_LIVE_TILES_EVENTS_FILE rejected: parent dir realpath failed. fallback to default.\n`
        )
        return DEFAULT_EVENTS_FILE
      }
    } else {
      process.stderr.write(
        `[append-event] VSCODE_LIVE_TILES_EVENTS_FILE rejected: realpath failed. fallback to default.\n`
      )
      return DEFAULT_EVENTS_FILE
    }
  }

  // home 自体が symlink 経由のケースに備えて realpath で解決
  let realHome
  try {
    realHome = realpathSync(homedir())
  } catch {
    realHome = homedir()
  }
  const homeWithSep = realHome.endsWith(sep) ? realHome : realHome + sep
  if (!realCandidate.startsWith(homeWithSep) && realCandidate !== realHome) {
    process.stderr.write(
      `[append-event] VSCODE_LIVE_TILES_EVENTS_FILE rejected: not under homedir (after symlink resolve). fallback to default.\n`
    )
    return DEFAULT_EVENTS_FILE
  }
  return realCandidate
}

async function readStdin() {
  if (process.stdin.isTTY) return ''
  const chunks = []
  for await (const chunk of process.stdin) chunks.push(chunk)
  return Buffer.concat(chunks).toString('utf8')
}

function parsePayload(raw) {
  if (!raw) return { sessionId: null, payload: {} }
  try {
    const obj = JSON.parse(raw)
    const sessionId = typeof obj?.session_id === 'string' ? obj.session_id : null
    return { sessionId, payload: obj }
  } catch {
    return { sessionId: null, payload: {} }
  }
}

/**
 * AGENTS.md の front matter から `project_name` を抽出する。
 * front matter が存在しない / project_name フィールドがない場合は null。
 */
function extractProjectNameFromFrontMatter(content) {
  // 先頭の `---\n ... \n---` を front matter とみなす（CRLF / LF 両対応）
  const match = content.match(/^---\r?\n([\s\S]*?)\r?\n---\s*(?:\r?\n|$)/)
  if (!match) return null
  const fm = match[1]
  const nameMatch = fm.match(/^project_name:\s*(.+?)\s*$/m)
  if (!nameMatch) return null
  return nameMatch[1].replace(/^["']|["']$/g, '').trim() || null
}

/**
 * AGENTS.md 本文の Markdown テーブル「| アプリ名 | XXX |」から値を抽出する。
 */
function extractProjectNameFromAppNameTable(content) {
  const match = content.match(/^\|\s*アプリ名\s*\|\s*([^|（(]+?)\s*(?:[（(]|\|)/m)
  if (!match) return null
  return match[1].trim() || null
}

function readPackageJsonName(cwd) {
  const pkgPath = join(cwd, 'package.json')
  if (!existsSync(pkgPath)) return null
  try {
    const pkg = JSON.parse(readFileSync(pkgPath, 'utf8'))
    return typeof pkg?.name === 'string' && pkg.name.length > 0 ? pkg.name : null
  } catch {
    return null
  }
}

/**
 * cwd からプロジェクト名を多段フォールバックで取得する。
 * - 1: AGENTS.md front matter `project_name`
 * - 2: AGENTS.md 本文「| アプリ名 | XXX |」テーブル
 * - 3: package.json name
 * - 4: path.basename(cwd)
 *
 * どんな I/O 失敗でも throw しない（exit 0 維持のため）。
 */
function resolveProjectName(cwd) {
  try {
    const agentsPath = join(cwd, 'AGENTS.md')
    if (existsSync(agentsPath)) {
      const content = readFileSync(agentsPath, 'utf8')
      const fromFm = extractProjectNameFromFrontMatter(content)
      if (fromFm) return fromFm
      const fromTable = extractProjectNameFromAppNameTable(content)
      if (fromTable) return fromTable
    }
  } catch {
    // 続行
  }

  const fromPkg = readPackageJsonName(cwd)
  if (fromPkg) return fromPkg

  try {
    return basename(cwd) || null
  } catch {
    return null
  }
}

/**
 * ウィジェットが読むのは background_tasks 各要素の status だけ。
 * status のみに剥がして返す（無ければ null = payload 自体を書かない）。
 */
function slimBackgroundTasks(payload) {
  const tasks = payload?.background_tasks
  if (!Array.isArray(tasks) || tasks.length === 0) return null
  const slim = tasks
    .filter((t) => t && typeof t.status === 'string')
    .map((t) => ({ status: t.status }))
  return slim.length > 0 ? slim : null
}

/**
 * サイズ上限を超えていたら events.jsonl → events.jsonl.1 にリネームして世代交代する。
 * 世代は 1 つだけ（.1 は上書き）。並列セッションと競合したら負けた側の rename が
 * 失敗するだけで、追記は新ファイルに落ちるので実害なし。失敗はすべて握りつぶす。
 */
function rotateIfNeeded(file) {
  try {
    if (statSync(file).size < MAX_FILE_BYTES) return
    const backup = file + '.1'
    try {
      unlinkSync(backup) // Windows は rename 先が存在すると失敗する
    } catch {
      // 続行（初回は存在しない）
    }
    renameSync(file, backup)
  } catch {
    // ファイル未存在・競合負けなど。追記側で吸収されるので何もしない
  }
}

async function main() {
  const type = process.argv[2]
  if (!VALID_TYPES.has(type)) return // unknown invocation: silently skip

  const raw = await readStdin().catch(() => '')
  const { sessionId, payload } = parsePayload(raw)

  // PreToolUse は AskUserQuestion のみ書く。matcher 漏れに対する二段防御として
  // payload.tool_name を必ず確認する。文字列以外 / 欠落はすべて捨てる。
  if (type === 'pre_tool_use') {
    const toolName = typeof payload?.tool_name === 'string' ? payload.tool_name : null
    if (toolName === null || !PRE_TOOL_USE_ALLOWED_TOOL_NAMES.has(toolName)) return
  }

  const cwd = process.cwd()
  const projectName = resolveProjectName(cwd)

  const record = {
    ts: Date.now(),
    type,
    sessionId,
    projectName,
    cwd
  }

  // stop の「背景タスクが残っていれば作業中」判定に必要な分だけ payload を残す
  const backgroundTasks = slimBackgroundTasks(payload)
  if (backgroundTasks) record.payload = { background_tasks: backgroundTasks }

  const file = resolveEventsFile()
  try {
    mkdirSync(dirname(file), { recursive: true })
    rotateIfNeeded(file)
    appendFileSync(file, JSON.stringify(record) + '\n', 'utf8')
  } catch {
    // swallow: never block Claude Code
  }
}

main().catch(() => {
  // never propagate
})
