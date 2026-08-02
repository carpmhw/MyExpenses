FROM node:24-alpine AS build
WORKDIR /app
COPY package*.json .
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY nginx-mode.conf /etc/nginx/conf.d/00-myexpenses-mode.conf
COPY entrypoint.sh /entrypoint.sh
EXPOSE 80
RUN chmod +x /entrypoint.sh
HEALTHCHECK --interval=10s --timeout=5s --retries=12 --start-period=5s CMD ["wget", "--quiet", "--header=X-Forwarded-Proto: https", "--output-document=/dev/null", "http://127.0.0.1/health/ready"]
ENTRYPOINT ["/entrypoint.sh"]
