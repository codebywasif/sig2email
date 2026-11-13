# Builds all three services. Pick one with --target api | smtp | worker.
#   docker build -f deploy/Dockerfile --target api -t sig2email-api .

FROM node:22-alpine AS portal
WORKDIR /web
COPY web/portal/package*.json ./
RUN npm ci --no-audit --no-fund
COPY web/portal/ ./
RUN npm run build

FROM node:22-alpine AS addin
WORKDIR /addin
COPY web/addin/package*.json ./
RUN npm ci --no-audit --no-fund
COPY web/addin/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Sig2Email.sln ./
COPY src/ src/
RUN dotnet publish src/Sig2Email.Api -c Release -o /out/api \
 && dotnet publish src/Sig2Email.MailProcessor -c Release -o /out/smtp \
 && dotnet publish src/Sig2Email.Worker -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=build /out/api ./
# The API serves the portal as a single-page app from wwwroot.
COPY --from=portal /web/dist ./wwwroot
# The Outlook add-in is served under /addin/ with the current settings filled in at request time.
COPY --from=addin /addin/dist ./addin
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Sig2Email.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS smtp
WORKDIR /app
COPY --from=build /out/smtp ./
ENV ASPNETCORE_URLS=http://+:8081 Smtp__Port=2525
# 2525 inside the container (non-root); publish it as port 25.
EXPOSE 2525 8081
USER $APP_UID
ENTRYPOINT ["dotnet", "Sig2Email.MailProcessor.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS worker
WORKDIR /app
COPY --from=build /out/worker ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Sig2Email.Worker.dll"]
