



# DEPLOY ON WHQUEUE
```bash
sudo ./deploy-canary.sh
```


   תיקון הבעלות עם דריסת השינויים המקומיים:
```bash
cd /opt/whqueue/repo

OWNER="$(stat -c '%U:%G' .)"
sudo chown -R "$OWNER" canary/ .git/

git config core.fileMode false

git fetch origin
git reset --hard origin/main
```

העלאת CONTAINER 
```bash
cd /opt/whqueue/repo/canary
chmod +x deploy-canary.sh set-canary-token.sh
grep -cE '\$\{[A-Z_]+:\?' canary.yml             # 0
grep -c 'empty in canary.env' deploy-canary.sh   # 1
sudo ./deploy-canary.sh
sudo docker service ls --filter name=canary_
```

TEST


```bash
R="$(sudo docker ps -q -f label=com.docker.swarm.service.name=canary_canary-runner | head -1)"; echo "runner: ${R:-NONE}"
sudo docker exec "$R" curl -s http://127.0.0.1:8080/version; echo
sudo docker exec "$R" curl -s http://canary-prober:8000/health | python3 -m json.tool
```
WAITING UNTIL UP

```bash
sleep 45
sudo docker service ls --filter name=canary_
sudo docker service ps canary_canary-runner canary_canary-prober --no-trunc \
  --format '{{.Name}} | {{.Node}} | {{.CurrentState}} | {{.Error}}' | head -6
```


 בודקים את הלוגים:

```bash
sudo docker service logs canary_canary-prober --tail 30 2>&1
sudo docker service logs canary_canary-runner --tail 30 2>&1


sudo docker service logs canary_canary-runner --tail 20 2>&1 | grep -i canary
sudo docker service logs canary_canary-prober --tail 40 2>&1 | grep -iE "consumer|subscribed|override|webhook|error"

```

## UPDATE TOKEN
```bash
sudo ./set-canary-token.sh
```

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
