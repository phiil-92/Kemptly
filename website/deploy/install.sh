#!/usr/bin/env bash
# Installs the Kemptly website on a Debian/Ubuntu server with nginx, optionally with free HTTPS (Let's Encrypt).
#
# Usage (from the website/ folder, on the server):
#   sudo ./deploy/install.sh kemptly.example.com              # HTTP only
#   sudo ./deploy/install.sh kemptly.example.com you@mail.com # HTTP + HTTPS via certbot
#
# Re-run it any time to publish changes to public/.
set -euo pipefail

DOMAIN="${1:-}"
EMAIL="${2:-}"
SITE_ROOT="/var/www/kemptly"
CONF="/etc/nginx/sites-available/kemptly"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ -z "$DOMAIN" ]]; then
  echo "Usage: sudo $0 <domain> [email-for-https]" >&2
  exit 1
fi
if [[ $EUID -ne 0 ]]; then
  echo "Please run with sudo." >&2
  exit 1
fi
if ! [[ "$DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]]; then
  echo "That doesn't look like a domain name: $DOMAIN" >&2
  exit 1
fi

# The Impressum contact email is kept out of git; it comes from website/.env (CONTACT_EMAIL=...).
CONTACT_EMAIL="$(sed -n 's/^CONTACT_EMAIL=//p' "$HERE/.env" 2>/dev/null | head -1)"
if ! [[ "$CONTACT_EMAIL" =~ ^[^[:space:]\|@]+@[^[:space:]\|@]+$ ]]; then
  echo "Please put your contact email into $HERE/.env, e.g.: CONTACT_EMAIL=you@example.com" >&2
  exit 1
fi

echo "==> Installing nginx"
apt-get update -qq
apt-get install -y -qq nginx rsync >/dev/null

echo "==> Copying website to $SITE_ROOT"
mkdir -p "$SITE_ROOT"
rsync -a --delete "$HERE/public/" "$SITE_ROOT/"
sed -i "s|__CONTACT_EMAIL__|$CONTACT_EMAIL|g" "$SITE_ROOT"/*.html
chown -R root:root "$SITE_ROOT"
find "$SITE_ROOT" -type d -exec chmod 755 {} +
find "$SITE_ROOT" -type f -exec chmod 644 {} +

echo "==> Writing nginx config for $DOMAIN"
sed -e "s|SERVER_NAME|$DOMAIN|" \
    -e "s|SITE_ROOT|$SITE_ROOT|" \
    -e "s|listen 8080;|listen 80;|" \
    -e "s|listen \[::\]:8080;|listen [::]:80;|" \
    -e "s|/var/log/nginx/access.log|/var/log/kemptly-web/access.log|" \
    "$HERE/deploy/nginx.conf" > "$CONF"

# Own access log, deleted daily (Ubuntu's default nginx logrotate would keep logs for 14 days).
mkdir -p /var/log/kemptly-web
cat > /etc/logrotate.d/kemptly-web <<'EOF'
/var/log/kemptly-web/access.log {
    daily
    rotate 0
    missingok
    notifempty
    postrotate
        invoke-rc.d nginx rotate >/dev/null 2>&1 || true
    endscript
}
EOF
ln -sf "$CONF" /etc/nginx/sites-enabled/kemptly
nginx -t
systemctl enable --now nginx >/dev/null
systemctl reload nginx

if [[ -n "$EMAIL" ]]; then
  echo "==> Getting a free HTTPS certificate from Let's Encrypt"
  apt-get install -y -qq certbot python3-certbot-nginx >/dev/null
  certbot --nginx --non-interactive --agree-tos --redirect -m "$EMAIL" -d "$DOMAIN"
  echo "    Certificates renew automatically (certbot.timer)."
fi

echo
echo "Done. Visit: http${EMAIL:+s}://$DOMAIN"
