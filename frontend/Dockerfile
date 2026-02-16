# Frontend (Angular) - build + serve static under /nodi/ (no nginx image needed)

FROM node:20-alpine AS build
WORKDIR /app

COPY frontend/package*.json ./
RUN npm ci

COPY frontend/ ./
# Build with correct base so assets resolve under /nodi/
RUN npm run build -- --configuration development --base-href /nodi/ --deploy-url /nodi/

FROM node:20-alpine
WORKDIR /app

# Static server
COPY frontend/static-server.mjs ./static-server.mjs

# Built assets
COPY --from=build /app/dist/frontend/browser/ ./dist/

ENV PORT=4200
ENV BASE_PATH=/nodi
ENV DIST_DIR=/app/dist

EXPOSE 4200
CMD ["node","./static-server.mjs"]
