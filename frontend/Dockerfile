# Contesto di build: root del repository (vedi infra/compose.yaml).
FROM node:24-alpine AS build
WORKDIR /app
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:stable-alpine AS runtime
COPY infra/web.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/bprojects/browser /usr/share/nginx/html
EXPOSE 8080
