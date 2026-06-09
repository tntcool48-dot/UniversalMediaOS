from fastapi import APIRouter
from curl_cffi import requests

router = APIRouter(
    prefix="/scrape",
    tags=["scrape"],
)

@router.get("/test")
def test_scrape():
    # Example using curl_cffi to bypass basic TLS fingerprinting
    # This simulates mimicking a Chrome browser
    try:
        response = requests.get("https://httpbin.org/headers", impersonate="chrome")
        return {"status": "success", "data": response.json()}
    except Exception as e:
        return {"status": "error", "message": str(e)}
