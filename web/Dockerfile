FROM node:22-alpine AS build
WORKDIR /web
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

# Unprivileged nginx: runs as a non-root user and listens on 8080.
FROM nginxinc/nginx-unprivileged:1.31.7-alpine3.24
ENV API_UPSTREAM=http://api:8080
ENV FRONT_DOOR_ID=
COPY --from=build /web/dist /usr/share/nginx/html
COPY nginx/security-headers.conf /etc/nginx/vpms/security-headers.conf
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
USER 101
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --retries=3 CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1
