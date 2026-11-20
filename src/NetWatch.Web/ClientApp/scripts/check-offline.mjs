// Build guard: fails the build if the bundled UI references any external http(s) resource.
// NetWatch runs on an air-gapped network, so every asset must be served by the service itself.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, extname } from 'node:path';

const dist = new URL('../dist/', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const checked = ['.html', '.css', '.js'];

// URLs that are allowed because they are never fetched (XML namespaces, licence comments, spec references).
const allowed = [
  /^https?:\/\/www\.w3\.org\//,
  /^https?:\/\/(www\.)?apache\.org\/licenses/,
  /^https?:\/\/reactjs\.org\/docs\/error-decoder/,
  /^https?:\/\/react\.dev\/errors/,
  /^https?:\/\/github\.com\//,
  /^https?:\/\/echarts\.apache\.org\//,
  /^https?:\/\/(www\.)?opensource\.org\//,
  /^https?:\/\/developer\.mozilla\.org\//,
  /^https?:\/\/fb\.me\//,
  /^https?:\/\/(www\.)?ecma-international\.org\//,
];

// Only flag URLs used as resource locations (src/href/url()/import/fetch), not arbitrary strings.
const resourcePattern = /(?:src=|href=|url\(|import\(|from\s|fetch\()\s*["'`]?(https?:\/\/[^"'`)\s]+)/g;

const problems = [];
function walk(dir) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      walk(full);
    } else if (checked.includes(extname(full))) {
      const text = readFileSync(full, 'utf8');
      for (const match of text.matchAll(resourcePattern)) {
        const url = match[1];
        if (!allowed.some((re) => re.test(url))) {
          problems.push(`${full}: ${url}`);
        }
      }
    }
  }
}

walk(dist);
if (problems.length > 0) {
  console.error('Offline check FAILED: external resources referenced in the bundle:');
  for (const p of problems) console.error('  ' + p);
  process.exit(1);
}
console.log('Offline check passed: no external resources referenced.');
