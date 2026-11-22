// Minimal HTTPS client for black-box tests against a running NetWatch instance (self-signed certificate, local only).
import https from 'node:https';

export class Client {
  constructor(base) { this.base = base; this.cookie = ''; }
  async req(method, path, body, extraHeaders = {}) {
    const url = new URL(path, this.base);
    const isBuf = Buffer.isBuffer(body);
    const data = body === undefined ? undefined : isBuf ? body : typeof body === 'string' ? body : JSON.stringify(body);
    const headers = { 'X-NetWatch-Request': '1', ...extraHeaders };
    if (data !== undefined && !headers['Content-Type']) headers['Content-Type'] = 'application/json';
    if (this.cookie) headers.Cookie = this.cookie;
    for (const k of Object.keys(headers)) if (headers[k] === null) delete headers[k];
    return new Promise((resolve, reject) => {
      const r = https.request(url, { method, headers, rejectUnauthorized: false, timeout: 120000, agent: false }, res => {
        const chunks = [];
        res.on('data', c => chunks.push(c));
        res.on('end', () => {
          const set = res.headers['set-cookie'];
          if (set) for (const c of set) {
            const [kv] = c.split(';'); const [k, v] = kv.split('=');
            const jar = Object.fromEntries(this.cookie ? this.cookie.split('; ').map(x => x.split(/=(.*)/s).slice(0, 2)) : []);
            if (v === '' || /expires=Thu, 01 Jan 1970/i.test(c)) delete jar[k]; else jar[k] = v;
            this.cookie = Object.entries(jar).map(([a, b]) => `${a}=${b}`).join('; ');
          }
          const buf = Buffer.concat(chunks);
          let json; try { json = JSON.parse(buf.toString('utf8')); } catch { }
          resolve({ status: res.statusCode, headers: res.headers, body: buf, text: buf.toString('utf8'), json });
        });
      });
      r.on('error', reject);
      r.on('timeout', () => r.destroy(new Error('timeout')));
      if (data !== undefined) r.write(data);
      r.end();
    });
  }
  get(p, h) { return this.req('GET', p, undefined, h); }
  post(p, b, h) { return this.req('POST', p, b ?? {}, h); }
  put(p, b, h) { return this.req('PUT', p, b ?? {}, h); }
  del(p, h) { return this.req('DELETE', p, undefined, h); }
  // The login rate limit (10/min per client address) is shared by every client of a test run: wait it out.
  async login(username, password) {
    for (let i = 0; ; i++) {
      const r = await this.post('/api/auth/login', { username, password });
      if (r.status !== 429 || i >= 2) return r;
      await new Promise(res => setTimeout(res, 61000));
    }
  }
}
