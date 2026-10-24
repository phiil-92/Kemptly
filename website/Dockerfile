# Kemptly website in a small, non-root nginx container (listens on port 8080).
# The contact email is kept out of git: put CONTACT_EMAIL=... into a .env file next to docker-compose.yml.
#   docker compose up -d --build
FROM nginxinc/nginx-unprivileged:1.27-alpine

COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY public/ /usr/share/nginx/html/

ARG CONTACT_EMAIL
USER root
# Docker networks usually have IPv6 off, so drop the [::] listener inside the container.
RUN test -n "$CONTACT_EMAIL" || { echo "CONTACT_EMAIL is not set (add it to .env)"; exit 1; } \
 && sed -i -e 's|SERVER_NAME|_|' -e 's|SITE_ROOT|/usr/share/nginx/html|' -e '/listen \[::\]/d' /etc/nginx/conf.d/default.conf \
 && sed -i "s|__CONTACT_EMAIL__|$CONTACT_EMAIL|g" /usr/share/nginx/html/*.html \
 && ! grep -l "__CONTACT_EMAIL__" /usr/share/nginx/html/*.html
USER 101

EXPOSE 8080
