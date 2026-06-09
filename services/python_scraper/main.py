from fastapi import FastAPI
from routers import scrape

app = FastAPI(title="Universal Media OS - Python Scraper", version="1.0.0")

app.include_router(scrape.router)

@app.get("/")
def read_root():
    return {"status": "ok", "service": "Python Scraper Engine"}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8000)
