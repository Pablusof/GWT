#!/usr/bin/env bash
set -euo pipefail

# Скрипт установки игрового сервера Ground War Tanks на Ubuntu 24.04 LTS (Google Cloud VPS)
# Использование:
#   sudo bash install_ubuntu.sh [ОПЦИОНАЛЬНО_ВНЕШНИЙ_IP] [ОПЦИОНАЛЬНО_ВКЛЮЧИТЬ_NGINX_CDN=yes|no]

PUBLIC_IP="${1:-}"
ENABLE_NGINX="${2:-no}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="/opt/gwt-server"
DATA_DIR="/opt/gwt-data"
CDN_DIR="/var/www/gwt-cdn"

if [ -z "$PUBLIC_IP" ]; then
  PUBLIC_IP="$(curl -fsS -H 'Metadata-Flavor: Google' http://metadata.google.internal/computeMetadata/v1/instance/network-interfaces/0/access-configs/0/external-ip 2>/dev/null || true)"
fi
if [ -z "$PUBLIC_IP" ]; then
  PUBLIC_IP="$(curl -fsS https://api.ipify.org 2>/dev/null || true)"
fi
if [ -z "$PUBLIC_IP" ]; then
  echo "Не удалось автоматически определить внешний IP. Запустите так:"
  echo "  sudo bash install_ubuntu.sh ВАШ_ВНЕШНИЙ_IP"
  exit 1
fi

echo "=== Установка GWT Server (Ubuntu 24.04) ==="
echo "Внешний IP (GWT_PUBLIC_IP): $PUBLIC_IP"

apt-get update -y
apt-get install -y ca-certificates libicu74 curl ufw

mkdir -p "$APP_DIR" "$DATA_DIR"
cp -r "$SCRIPT_DIR/app/." "$APP_DIR/"
chmod +x "$APP_DIR/GwtEmulator.App"

# Отдельный сервис systemd с автоперезапуском
cat >/etc/systemd/system/gwt-server.service <<EOF
[Unit]
Description=Ground War Tanks Emulator Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/GwtEmulator.App
Environment=GWT_PUBLIC_IP=$PUBLIC_IP
Environment=GWT_DATA_DIR=$DATA_DIR
Restart=always
RestartSec=3
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable gwt-server
systemctl restart gwt-server

# Открываем порты локального UFW, если он активен (в Google Cloud VPC Firewall также нужно открыть их вручную)
if ufw status | grep -q "Status: active"; then
  ufw allow 9801/tcp
  ufw allow 9301/tcp
  ufw allow 9601/tcp
  ufw allow 18080/tcp
  ufw allow 18081/tcp
  [ "$ENABLE_NGINX" = "yes" ] && ufw allow 80/tcp
fi

if [ "$ENABLE_NGINX" = "yes" ]; then
  apt-get install -y nginx
  mkdir -p "$CDN_DIR/files"
  cat >/etc/nginx/sites-available/gwt-cdn <<EOF
server {
    listen 80 default_server;
    server_name _;
    root $CDN_DIR;
    autoindex off;
    location / {
        try_files \$uri =404;
    }
}
EOF
  ln -sf /etc/nginx/sites-available/gwt-cdn /etc/nginx/sites-enabled/default
  systemctl restart nginx
  echo "Nginx включён! Загрузите manifest.json в $CDN_DIR/manifest.json, а файлы игры в $CDN_DIR/files/"
fi

sleep 2
systemctl --no-pager -l status gwt-server || true
echo ""
echo "Готово! Логи смотреть командой: journalctl -u gwt-server -f"
echo "Не забудьте открыть TCP-порты 9801, 9301, 9601, 18080, 18081 (и 80 для Nginx) в Google Cloud VPC Firewall!"
