



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
TEST SENDING

```bash
R="$(sudo docker ps -q -f label=com.docker.swarm.service.name=canary_canary-runner | head -1)"

# 1. מה יש בתבנית: שפה מדויקת ופרמטרים
sudo docker exec "$R" curl -s http://canary-prober:8000/templates/3p_direct_integration_test_template \
  | python3 -m json.tool | grep -E '"language"|"status"|"text"|\{\{'

# 2. שליחה לטלפון שלך (בלי params אם אין {{1}} בטקסט)
sudo docker exec "$R" curl -s -X POST http://canary-prober:8000/send/template \
  -H 'Content-Type: application/json' \
  -d '{"jid":"972546252491","name":"3p_direct_integration_test_template","language":"en_US"}'; echo


```


TEST ACK
```bash
sudo docker service logs canary_canary-prober --since 5m 2>&1 | grep -vE "GET /health|graph.facebook" | tail -15
sudo docker service logs canary_canary-runner --since 5m 2>&1 | grep -iE "ACK|inbound|CANARY" | tail -10
```


## UPDATE TOKEN
```bash
sudo ./set-canary-token.sh
```

TEST SYNC!!!

STOP CANARY
```bash
sudo docker service scale canary_canary-runner=0
```


REMOVE  CANARY
```bash
sudo docker stack rm canary
```

```bash
select created_at, user_id, phone_id, title, message, log_level, is_read
from notifications where source = 'canary' order by created_at desc limit 20;
```
