FROM python:3.12-slim
ARG GIT_SHA=unknown
ENV RELEASE_GIT_SHA=$GIT_SHA
WORKDIR /app
COPY ai/icehott-ai-service/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY ai/icehott-ai-service/app ./app
EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
