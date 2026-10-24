# Kemptly website

A fast, static landing page for Kemptly: plain HTML, CSS and a few lines of JavaScript.
No build step, no external fonts, CDNs or trackers. The download buttons always point to the newest
GitHub release, so the site doesn't need updating when you publish a new version.

```
website/
  public/          The site itself - this is the only folder that gets served
  deploy/
    nginx.conf     nginx config with strict security headers
    install.sh     One-command setup for Debian/Ubuntu (nginx + optional HTTPS)
    Caddyfile      Alternative: Caddy with automatic HTTPS
  Dockerfile       Alternative: non-root nginx container
  docker-compose.yml
```

Before you start, point your domain's DNS **A record** (and AAAA for IPv6) at the server's IP,
and open ports **80** and **443** in its firewall.

## Option 1: nginx on Debian/Ubuntu (recommended)

Copy the `website` folder to the server, then:

```bash
cd website
chmod +x deploy/install.sh
sudo ./deploy/install.sh kemptly.example.com you@example.com
```

This installs nginx, copies `public/` to `/var/www/kemptly`, sets up the site and gets a free
Let's Encrypt HTTPS certificate that renews automatically. Leave out the email for HTTP only.
Run the same command again whenever you change the site.

## Option 2: Docker

```bash
cd website
docker compose up -d --build
```

The site runs on port 8080 in a read-only, non-root container. For HTTPS, put it behind
your existing reverse proxy (Caddy, Traefik, nginx Proxy Manager, ...), or change the port
mapping in `docker-compose.yml` to `"80:8080"` to serve plain HTTP directly.

## Option 3: Caddy (automatic HTTPS)

Install [Caddy](https://caddyserver.com/docs/install), copy `public/` to `/var/www/kemptly`,
put your domain into `deploy/Caddyfile`, copy it to `/etc/caddy/Caddyfile` and run
`sudo systemctl reload caddy`. Caddy fetches and renews the certificate by itself.

## Contact email

The contact email shown in the Impressum and privacy policy is not stored in git. The HTML contains the
placeholder `__CONTACT_EMAIL__`, which is filled in when the site is built or installed. Create a file
`website/.env` (it is git-ignored) with:

```
CONTACT_EMAIL=you@example.com
```

Docker (`docker compose up -d --build`) and `deploy/install.sh` both read it and refuse to start without it.

## Preview locally

```bash
cd website/public
python3 -m http.server 8000
```

Then open http://localhost:8000.

## After you have a domain

Social networks need absolute URLs for the link preview image. In `public/index.html`, change
`<meta property="og:image" content="assets/og-image.png">` to your full URL, for example
`https://kemptly.example.com/assets/og-image.png`.

## Updating

After changing `style.css` or `main.js`, raise the `?v=` number where they're linked in the HTML files
(for example `style.css?v=2` to `style.css?v=3`). Browsers cache these files for a week, and the new
number makes them fetch the fresh version right away.

## Legal pages (Germany)

`public/impressum.html` and `public/datenschutz.html` contain highlighted placeholders like
`[Vorname Nachname]`. Replace all of them with your details before the site goes public.
The access log stores only shortened IP addresses and is emptied every night by
`deploy/clear-logs.cron` (install it with `sudo cp deploy/clear-logs.cron /etc/cron.d/kemptly-web-logs`),
which is what the privacy policy describes. If you put a proxy or CDN in front of the site,
mention it in section 3 of the privacy policy and keep its logs short as well.
