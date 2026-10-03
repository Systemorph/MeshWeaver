# s1-extract — GLiNER multi v2.1 (Apache-2.0) on ONNX Runtime, as a stateless in-cluster extractor.
# The model is fetched and EXPORTED at build time and baked in; the runtime never downloads anything
# (same reasoning as deploy/whisper: the registry the pod already pulls from is the only channel).
#
#   docker build -t s1-extract:dev deploy/s1-extract
#   az acr build -r <registry> -t s1-extract:gliner-multi-2.1 deploy/s1-extract

FROM python:3.12-slim AS base
ENV PIP_NO_CACHE_DIR=1 PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
RUN pip install --index-url https://download.pytorch.org/whl/cpu torch==2.8.0
COPY requirements.txt /tmp/requirements.txt
RUN pip install -r /tmp/requirements.txt

FROM base AS export
RUN pip install onnx==1.19.0 onnxscript==0.5.4
COPY export_model.py /tmp/export_model.py
RUN python /tmp/export_model.py /models/gliner

FROM base
COPY --from=export /models/gliner /models/gliner
COPY app /app
WORKDIR /app
# Offline tests of the service surface, in the image that ships (fake model, no network).
# httpx stays: huggingface_hub (via transformers) imports it when the model LOADS. Uninstalling it
# after these tests shipped an image whose model never loaded (/readyz 503 forever).
RUN S1_NO_AUTOSTART=1 python test_server.py
# The REAL model through the server's REAL load path, offline, in the stage that ships.
RUN S1_NO_AUTOSTART=1 S1_MODEL_DIR=/models/gliner HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 python smoke_model.py
RUN useradd --uid 10001 --no-create-home s1 && chown -R s1 /app
USER 10001
ENV S1_MODEL_DIR=/models/gliner HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 OMP_NUM_THREADS=4
EXPOSE 8080
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8080", "--workers", "1"]
