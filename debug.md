בדפדפן ב-NUC: http://localhost:8090

או בטרמינל שני (לא באותו חלון):

```bash
curl -s -o /dev/null -w '%{http_code}\n' localhost:8090/health


curl -s -u admin:"$AUTH_PASSWORD" localhost:8090/api/hosts | jq '.items[] | {host_name, probe: .probe.reachable}'

```

```bash

sleep 25
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
cd /opt/phone-orchestrator
git pull
./deploy.sh
```
