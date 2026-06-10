# This script builds the Python microservice into a standalone executable
# Ensure you have installed pyinstaller: pip install pyinstaller

# Run from within services\python_scraper
pyinstaller --name "python_scraper" --onefile main.py

# The executable will be in the 'dist' folder
