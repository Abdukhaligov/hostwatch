# Build context is the project root (see docker-compose.yml).
FROM python:3.12-slim
WORKDIR /app
COPY server/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY server/app.py server/demo.py ./
COPY server/static ./static
COPY agent ./agent
ENV HOSTWATCH_PORT=8765
# Adds the TLS flags only when HOSTWATCH_TLS_CERT is set (see docker-compose.yml), otherwise plain http.
CMD ["sh", "-c", "uvicorn app:app --host 0.0.0.0 --port \"$HOSTWATCH_PORT\" --log-level warning ${HOSTWATCH_TLS_CERT:+--ssl-certfile \"$HOSTWATCH_TLS_CERT\" --ssl-keyfile \"$HOSTWATCH_TLS_KEY\"}"]
