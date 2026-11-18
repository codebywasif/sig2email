#!/bin/sh
# Certbot deploy hook: hands a renewed SMTP certificate to Sig2Email.
#
# Install on the Docker host (certbot runs every hook in this folder after each successful renewal):
#   install -m 755 deploy/certbot-deploy-hook.sh /etc/letsencrypt/renewal-hooks/deploy/sig2email-smtp
# First issue of the certificate (port 80 for the name must reach this host's nginx):
#   certbot certonly --nginx -d "$S2E_SMTP_NAME" --deploy-hook /etc/letsencrypt/renewal-hooks/deploy/sig2email-smtp
#
# The certificate goes into Sig2Email's settings exactly like an upload under Settings → System, so the mail
# processor starts using it within seconds and no container is restarted. Exchange Online matches the connector
# on the certificate's name, which does not change on renewal.
set -eu

S2E_SMTP_NAME="${S2E_SMTP_NAME:-smtp-sig2email.dgcars.cloud}"
S2E_DIR="${S2E_DIR:-/var/app/sig2email}"

# Certbot sets these for deploy hooks; other certificates on this host are none of our business.
case " ${RENEWED_DOMAINS:-} " in
  *" $S2E_SMTP_NAME "*) ;;
  *) exit 0 ;;
esac

cd "$S2E_DIR"
openssl pkcs12 -export -inkey "$RENEWED_LINEAGE/privkey.pem" -in "$RENEWED_LINEAGE/fullchain.pem" -passout pass: \
  | docker compose -f docker-compose.yml -f docker-compose.prod.yml exec -T api dotnet Sig2Email.Api.dll install-certificate
