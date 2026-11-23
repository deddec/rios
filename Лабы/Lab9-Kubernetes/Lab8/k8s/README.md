# ЛР №9 — развертывание сервера FlowerShop в Kubernetes

Манифесты и последовательность действий для всех частей ЛР №9. Предметная область — вариант 14 («Заказ и доставка цветов»), образ — из ЛР №8.

## Файлы

| Файл | Назначение |
|---|---|
| `part1-deployment.yaml` | Deployment без ConfigMap (часть 1) |
| `deployment.yaml` | Итоговый Deployment с `APP_MODE` из ConfigMap (части 4) |
| `service.yaml` | Service типа ClusterIP, порт 5000 |
| `configmap.yaml` | ConfigMap `flower-shop-config` с `APP_MODE: "kubernetes"` |

Код: `Server/AppConfig.cs` читает `APP_MODE` (по умолчанию `default`); `Server/Program.cs` выводит значение в консоль и в журнал `consensus.log`.

## Часть 0. Образ и кластер

```bash
# 1. Собрать образ из ЛР №8 и присвоить тег 1.0
docker build -t flower-shop-server .
docker tag flower-shop-server flower-shop-server:1.0
docker images | grep flower-shop-server

# 2a. Docker Desktop: Settings → Kubernetes → Enable Kubernetes.
#     Локальный образ виден кластеру автоматически.
# 2b. Minikube:
minikube start
minikube image load flower-shop-server:1.0

# 3. Проверка кластера и контекста
kubectl config current-context
kubectl cluster-info
kubectl get nodes
```

Проверка приложения до развертывания (ЛР №8): `docker run -p 5000:5000 flower-shop-server:1.0`, затем клиент `dotnet run --project Client 127.0.0.1 5000`.

## Часть 1. Deployment

```bash
kubectl apply -f k8s/part1-deployment.yaml
kubectl get deployments
kubectl get pods
kubectl describe pod <имя-pod>
kubectl logs <имя-pod>
kubectl get events --sort-by=.metadata.creationTimestamp
```

## Часть 2. Service и доступ с локального компьютера

```bash
kubectl apply -f k8s/service.yaml
kubectl get services
kubectl describe service flower-shop-server
kubectl get pods --show-labels                 # метки Pod должны совпадать с selector Service
kubectl port-forward service/flower-shop-server 5000:5000   # оставить работать в отдельном терминале

# в другом терминале
dotnet run --project Client 127.0.0.1 5000
```

## Часть 3. Масштабирование и восстановление

```bash
kubectl scale deployment flower-shop-server --replicas=3
kubectl get deployments
kubectl get pods -w                            # Ctrl+C для выхода

kubectl scale deployment flower-shop-server --replicas=2
kubectl get pods

kubectl scale deployment flower-shop-server --replicas=3
kubectl get pods

# Моделирование отказа
kubectl get pods
kubectl delete pod <имя-одного-pod>
kubectl get pods -w                            # наблюдать: удаленный Pod, новый Pod, Running
kubectl get events --sort-by=.metadata.creationTimestamp
```

Проверка доступности после восстановления — клиент через `port-forward` (`port-forward` привязан к Service, а не к конкретному Pod; при удалении Pod его перенаправление продолжает работать через другой экземпляр).

## Часть 4. ConfigMap и намеренная ошибка

```bash
kubectl apply -f k8s/configmap.yaml
kubectl get configmap flower-shop-config -o yaml

kubectl apply -f k8s/deployment.yaml
kubectl get pods
kubectl logs <имя-pod> | findstr "mode"       # Windows PowerShell: Select-String "mode"
# ожидаемо в журнале: "Application mode: kubernetes"

# Проверка конфигурации в самом Pod
kubectl exec <имя-pod> -- printenv APP_MODE
```

Изменение ConfigMap не попадает в уже запущенные Pod. Чтобы применить новое значение:

```bash
kubectl rollout restart deployment flower-shop-server
```

### Намеренная ошибка: несуществующий тег образа

```bash
# временно заменить тег в deployment.yaml на несуществующий (например, 9.9)
sed -i 's/flower-shop-server:1.0/flower-shop-server:9.9/' k8s/deployment.yaml   # Linux/macOS
# Windows PowerShell:
#   (Get-Content k8s/deployment.yaml) -replace 'flower-shop-server:1.0','flower-shop-server:9.9' | Set-Content k8s/deployment.yaml

kubectl apply -f k8s/deployment.yaml
kubectl get pods                               # ожидаемо ErrImagePull / ImagePullBackOff
kubectl describe pod <имя-pod>                 # раздел Events: сообщение о неудачной загрузке образа
kubectl get events --sort-by=.metadata.creationTimestamp

# Исправление: вернуть тег 1.0
sed -i 's/flower-shop-server:9.9/flower-shop-server:1.0/' k8s/deployment.yaml
kubectl apply -f k8s/deployment.yaml
kubectl rollout status deployment flower-shop-server
kubectl get deployments
kubectl get pods
```

## Очистка

```bash
kubectl delete -f k8s/service.yaml
kubectl delete -f k8s/deployment.yaml
kubectl delete -f k8s/configmap.yaml
```

## Примечания

- Сервер FlowerShop без MSSQL работает: при недоступной БД он пишет `MSSQL недоступен` и продолжает работу с заказами в памяти. В кластере БД не развернута — это естественный пример зависимого компонента (часть 3, требование 7).
- Порт в ЛР — 5000 (как в Dockerfile ЛР №8), а не 8080 из примера в методичке.
- Имена ресурсов: Deployment и Service — `flower-shop-server`, ConfigMap — `flower-shop-config`.
