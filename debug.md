בדפדפן ב-NUC: http://localhost:8090

או בטרמינל שני (לא באותו חלון):

```bash
curl -s -o /dev/null -w '%{http_code}\n' localhost:8090/health


curl -s -u admin:"$AUTH_PASSWORD" localhost:8090/api/hosts | jq '.items[] | {host_name, probe: .probe.reachable}'

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


```bash
git pull && ./deploy.sh
sleep 30
curl -s localhost:8090/version | jq
```

```bash
cd /opt/phone-orchestrator
git checkout -- deploy.sh
git pull
chmod +x deploy.sh
./deploy.sh
```
