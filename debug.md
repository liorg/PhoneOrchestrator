
# DEPLOY ON WEB (MICHAL)

## HEALTH
```bash
curl -s -o /dev/null -w '%{http_code}\n' localhost:8090/health


curl -s -u admin:"$AUTH_PASSWORD" localhost:8090/api/hosts | jq '.items[] | {host_name, probe: .probe.reachable}'

```

ה-service עלה, אבל הוא ב-0/1, כלומר ה-task לא רץ, ולכן /version לא ענה. הסיבה מופיעה באחת משתי הפקודות האלה:

```bash
sudo docker service ps orchestrator_orchestrator --no-trunc --format '{{.CurrentState}} | {{.Error}}' | head -5
sudo docker service logs orchestrator_orchestrator --tail 40 2>&1 | tail -40
```


```bash
docker service ps orchestrator_orchestrator | head -3
curl -s -o /dev/null -w '%{http_code}\n' localhost:8090/api/hosts
curl -s -o /dev/null -w '%{http_code}\n' -u admin:'<סיסמה>' localhost:8090/api/hosts
curl -s localhost:8090/health

```


```bash
docker service ps orchestrator_orchestrator --no-trunc | head -5
docker service logs orchestrator_orchestrator --tail 20
```

```bash
curl -s http://localhost:8090/version

```
תריץ רק את השתיים ותדביק:
הפקודות האלה נועדו לבדוק האם login.html באמת נכנס ל־Docker image.
```bash
cd /opt/phone-orchestrator
docker build --no-cache -t oc-test . 2>&1 | tail -25
echo "=== wwwroot in image ==="
docker run --rm oc-test ls -la wwwroot
```

get from hub and deploy
```bash
cd /opt/phone-orchestrator
sudo git checkout -- deploy.sh
sudo git pull
sudo grep -n 'Version = ' src/Models/Dtos.cs
sudo git log --oneline -3
sudo chmod +x deploy.sh
```

```bash
cd /opt/phone-orchestrator
git checkout -- deploy.sh
git pull
chmod +x deploy.sh
./deploy.sh
```

test and log

```bash
curl -s localhost:8090/version; echo
sudo bash -c 'source .env; curl -s -u "${AUTH_USER:-admin}:$AUTH_PASSWORD" localhost:8090/api/canary/control' \
  | python3 -m json.tool | grep -E '"runnerAlive"|"runner_mode"|"prober_ready"|"enabled"|"runner_version"'
sudo docker service logs orchestrator_orchestrator --since 2m 2>&1 | grep -c "botinbound unavailable"
```