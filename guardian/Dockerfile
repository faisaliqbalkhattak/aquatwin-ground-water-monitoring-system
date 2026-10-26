FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt && useradd --uid 10001 --create-home guardian && mkdir -p /data && chown guardian:guardian /data
COPY guardian_core ./guardian_core
USER guardian
ENV PYTHONUNBUFFERED=1 AQUATWIN_DATABASE=/data/guardian.sqlite3
VOLUME /data
CMD ["python", "-m", "guardian_core", "run", "--config", "/config/settings.json"]
