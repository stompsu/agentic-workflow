# syntax=docker/dockerfile:1
# Build context: src/frontend.  Image name: agentic-workflow-frontend.
#   docker build -t agentic-workflow-frontend src/frontend
#   docker run -p 8080:8080 -e BACKEND_UPSTREAM=... -e AUTH_CLIENT_ID=... -e AUTH_AUTHORITY=... -e AUTH_API_SCOPE=... agentic-workflow-frontend
# Nothing auth-related is baked at build: /config.json is written at container start.

FROM node:26.12.1-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# tsc + vite build + dist check (no e2e mock-auth code in the bundle)
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.31.7-alpine AS runtime
USER root
# jq: JSON-escaping when generating config.json.
RUN apk add --no-cache jq \
 && rm -f /etc/nginx/conf.d/default.conf \
 && mkdir -p /etc/nginx/snippets \
 && chown -R nginx:root /etc/nginx/conf.d /usr/share/nginx/html \
 && chmod -R g+w /etc/nginx/conf.d /usr/share/nginx/html
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY nginx/snippets/ /etc/nginx/snippets/
# http-context map for $client_ip and the access log format without client IPs (static, not a template).
COPY --chown=nginx:root nginx/conf.d/client-ip-map.conf /etc/nginx/conf.d/client-ip-map.conf
COPY docker-entrypoint.d/10-write-config.sh /docker-entrypoint.d/10-write-config.sh
COPY --from=build --chown=nginx:root /app/dist/ /usr/share/nginx/html/
RUN chmod 755 /docker-entrypoint.d/10-write-config.sh
# Only BACKEND_* variables are substituted into the nginx template ($host etc. stay nginx variables).
ENV NGINX_ENVSUBST_FILTER=BACKEND_
USER nginx
EXPOSE 8080
