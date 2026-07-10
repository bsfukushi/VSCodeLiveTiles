#!/usr/bin/env node
// VSCode Live Tiles: CC 状態バッジ用フックを ~/.claude/settings.json に登録する。
//
// Usage:
//   node hooks/install.mjs           # 登録（再実行しても重複しない）
//   node hooks/install.mjs --dry-run # 書き込まずに変更内容を表示
//
// やること:
//   1. ~/.claude/settings.json を読む（無ければ新規作成）
//   2. 既存の append-event.mjs 登録（旧パス・CCPet 版含む）を全部取り除く
//   3. このリポジトリの hooks/append-event.mjs を 9 イベントに登録し直す
//   4. 書き込み前に settings.json.vscode-live-tiles.bak へバックアップ
//   5. ~/.vscode-live-tiles/ を作成（ウィジェットが起動時から監視できるように）
//
// 反映は即時〜次のセッション開始（実測では既存セッションも次のフック発火から新設定を拾った）。
// 他のフック（通知音など）には触らない。append-event.mjs を含む command だけを対象にする。

import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const DRY_RUN = process.argv.includes('--dry-run')

const SETTINGS_FILE = join(homedir(), '.claude', 'settings.json')
const EVENTS_DIR = join(homedir(), '.vscode-live-tiles')

// このスクリプト自身の場所から append-event.mjs の絶対パスを導く（クローン先がどこでも動く）
const HOOK_SCRIPT = join(dirname(fileURLToPath(import.meta.url)), 'append-event.mjs')

/** settings.json のイベント名 → append-event.mjs に渡す type。 */
const EVENT_MAP = [
  ['SessionStart', 'session_start'],
  ['SessionEnd', 'session_end'],
  ['UserPromptSubmit', 'user_prompt_submit'],
  ['Stop', 'stop'],
  ['Notification', 'notification'],
  ['PermissionRequest', 'permission_request'],
  ['PreToolUse', 'pre_tool_use'], // matcher: AskUserQuestion（質問待ちの検知のみ）
  ['PostToolUse', 'post_tool_use'],
  ['PostToolUseFailure', 'post_tool_use_failure']
]

function fail(message) {
  console.error(`[install] ERROR: ${message}`)
  process.exit(1)
}

if (!existsSync(HOOK_SCRIPT)) fail(`append-event.mjs が見つかりません: ${HOOK_SCRIPT}`)

// パス区切りはスラッシュに統一（settings.json 内での可読性とエスケープ回避）
const hookScriptForCommand = HOOK_SCRIPT.replaceAll('\\', '/')
const buildCommand = (type) => `node "${hookScriptForCommand}" ${type}`

// --- 1. settings.json を読む ---
let settings = {}
if (existsSync(SETTINGS_FILE)) {
  try {
    settings = JSON.parse(readFileSync(SETTINGS_FILE, 'utf8'))
  } catch (err) {
    fail(`settings.json のパースに失敗しました。壊さないよう中断します: ${err.message}`)
  }
  if (settings === null || typeof settings !== 'object' || Array.isArray(settings)) {
    fail('settings.json のルートがオブジェクトではありません。中断します。')
  }
}

if (typeof settings.hooks !== 'object' || settings.hooks === null) settings.hooks = {}
const hooks = settings.hooks

// --- 2. 既存の append-event.mjs 登録を全イベントから取り除く ---
const isOurs = (h) => typeof h?.command === 'string' && h.command.includes('append-event.mjs')
let removed = 0
for (const eventName of Object.keys(hooks)) {
  const groups = hooks[eventName]
  if (!Array.isArray(groups)) continue
  for (const group of groups) {
    if (!Array.isArray(group?.hooks)) continue
    const before = group.hooks.length
    group.hooks = group.hooks.filter((h) => !isOurs(h))
    removed += before - group.hooks.length
  }
  // 空になったグループは捨てる（他のフックが残るグループには触らない）
  hooks[eventName] = groups.filter((g) => Array.isArray(g?.hooks) && g.hooks.length > 0)
  if (hooks[eventName].length === 0) delete hooks[eventName]
}

// --- 3. 9 イベントに登録し直す ---
for (const [eventName, type] of EVENT_MAP) {
  const group = { hooks: [{ type: 'command', command: buildCommand(type) }] }
  if (eventName === 'PreToolUse') group.matcher = 'AskUserQuestion'
  if (!Array.isArray(hooks[eventName])) hooks[eventName] = []
  hooks[eventName].push(group)
}

// --- 4. バックアップして書き込み ---
const json = JSON.stringify(settings, null, 2) + '\n'
if (DRY_RUN) {
  console.log(`[install] --dry-run: 書き込みは行いません（既存の append-event 登録 ${removed} 件を置き換え予定）`)
  console.log(json)
  process.exit(0)
}

if (existsSync(SETTINGS_FILE)) {
  copyFileSync(SETTINGS_FILE, SETTINGS_FILE + '.vscode-live-tiles.bak')
} else {
  mkdirSync(dirname(SETTINGS_FILE), { recursive: true })
}
writeFileSync(SETTINGS_FILE, json, 'utf8')

// --- 5. イベント置き場を用意（ウィジェットは起動時にこのディレクトリの有無でバッジ機能を判定する） ---
mkdirSync(EVENTS_DIR, { recursive: true })

console.log(`[install] 完了: ${EVENT_MAP.length} イベントを登録しました（旧登録 ${removed} 件を置き換え）`)
console.log(`[install]   hook  : ${HOOK_SCRIPT}`)
console.log(`[install]   events: ${join(EVENTS_DIR, 'events.jsonl')}`)
console.log(`[install]   backup: ${SETTINGS_FILE}.vscode-live-tiles.bak`)
console.log('[install] 反映されない場合は Claude Code セッションを新しく開始。ウィジェットも再起動してください。')
