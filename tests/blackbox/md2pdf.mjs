// Converts a Markdown document (the subset used in docs/) to a self-contained HTML file and prints it to PDF with
// a locally installed Edge/Chrome in headless mode. No network access; no packages needed.
// usage: node md2pdf.mjs <in.md> <out.pdf> [title]
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFileSync } from 'node:child_process';

const [input, output, title] = process.argv.slice(2);
const md = fs.readFileSync(input, 'utf8').replace(/\r\n/g, '\n');
const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const inline = s => esc(s)
  .replace(/`([^`]+)`/g, '<code>$1</code>')
  .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
  .replace(/(^|[^*])\*([^*\s][^*]*)\*/g, '$1<em>$2</em>')
  .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<span class="link">$1</span>')
  .replace(/\\([<>|*_\[\]])/g, '$1');

const lines = md.split('\n');
const out = [];
let i = 0;
while (i < lines.length) {
  const l = lines[i];
  if (/^```/.test(l)) {
    const code = [];
    for (i++; i < lines.length && !/^```/.test(lines[i]); i++) code.push(lines[i]);
    out.push(`<pre>${esc(code.join('\n'))}</pre>`); i++; continue;
  }
  const h = l.match(/^(#{1,4})\s+(.*)/);
  if (h) { out.push(`<h${h[1].length}>${inline(h[2])}</h${h[1].length}>`); i++; continue; }
  if (/^\|/.test(l) && i + 1 < lines.length && /^\|[\s:|-]+\|?$/.test(lines[i + 1])) {
    const cells = r => r.replace(/^\||\|$/g, '').split(/(?<!\\)\|/).map(c => inline(c.trim()));
    const head = cells(l); const rows = [];
    for (i += 2; i < lines.length && /^\|/.test(lines[i]); i++) rows.push(cells(lines[i]));
    out.push(`<table><thead><tr>${head.map(c => `<th>${c}</th>`).join('')}</tr></thead><tbody>${rows.map(r => `<tr>${r.map(c => `<td>${c}</td>`).join('')}</tr>`).join('')}</tbody></table>`);
    continue;
  }
  if (/^\s*([-*]|\d+\.)\s+/.test(l)) {
    const ordered = /^\s*\d+\./.test(l); const items = [];
    while (i < lines.length && (/^\s*([-*]|\d+\.)\s+/.test(lines[i]) || (/^\s{2,}\S/.test(lines[i]) && items.length))) {
      const m = lines[i].match(/^\s*(?:[-*]|\d+\.)\s+(.*)/);
      if (m) items.push(m[1]); else items[items.length - 1] += ' ' + lines[i].trim();
      i++;
    }
    const li = items.map(t => { const c = t.match(/^\[( |x)\]\s+(.*)/i); return c ? `<li class="task"><span class="box">${c[1] === ' ' ? '☐' : '☑'}</span> ${inline(c[2])}</li>` : `<li>${inline(t)}</li>`; });
    out.push(ordered ? `<ol>${li.join('')}</ol>` : `<ul>${li.join('')}</ul>`); continue;
  }
  if (/^---+$/.test(l)) { out.push('<hr>'); i++; continue; }
  if (l.trim() === '') { i++; continue; }
  const para = [];
  while (i < lines.length && lines[i].trim() !== '' && !/^(#|\||```|\s*([-*]|\d+\.)\s)/.test(lines[i])) para.push(lines[i++].trim());
  out.push(`<p>${inline(para.join(' '))}</p>`);
}

const html = `<!doctype html><html><head><meta charset="utf-8"><title>${esc(title ?? path.basename(input))}</title><style>
@page { size: A4; margin: 16mm 14mm; }
body { font-family: "Segoe UI", Arial, sans-serif; font-size: 10pt; color: #1a1a1a; line-height: 1.45; }
h1 { font-size: 18pt; border-bottom: 2px solid #1f4e79; padding-bottom: 4px; color: #1f4e79; }
h2 { font-size: 13pt; color: #1f4e79; margin-top: 18px; page-break-after: avoid; }
h3 { font-size: 11pt; page-break-after: avoid; }
table { border-collapse: collapse; width: 100%; margin: 8px 0; page-break-inside: auto; }
th, td { border: 1px solid #b7c3d0; padding: 3px 6px; vertical-align: top; text-align: left; font-size: 9pt; }
th { background: #e8eef5; } tr { page-break-inside: avoid; }
code, pre { font-family: Consolas, monospace; font-size: 8.5pt; background: #f3f5f7; }
pre { padding: 6px 8px; white-space: pre-wrap; border: 1px solid #dde3e9; }
li.task { list-style: none; margin-left: -18px; } .box { font-size: 11pt; }
.link { text-decoration: underline; }
</style></head><body>${out.join('\n')}</body></html>`;
const tmp = path.join(os.tmpdir(), 'nw-md2pdf-' + process.pid + '.html');
fs.writeFileSync(tmp, html);
const browsers = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Microsoft/Edge/Application/msedge.exe'];
const exe = browsers.find(b => fs.existsSync(b));
if (!exe) throw new Error('No Edge or Chrome found');
execFileSync(exe, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-pdf-header-footer', `--user-data-dir=${path.join(os.tmpdir(), 'nw-md2pdf-profile')}`, `--print-to-pdf=${path.resolve(output)}`, 'file:///' + tmp.replace(/\\/g, '/')], { stdio: 'ignore', timeout: 120000 });
fs.rmSync(tmp);
console.log(`${output}: ${fs.statSync(output).size} bytes`);
