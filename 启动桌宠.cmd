@echo off
rem 双击启动桌宠。已经在跑的话，会让那只冒泡说"我在这儿"，
rem 万一它跑到屏幕外了会自己挪回右下角。
start "" "%~dp0bin\Pet.exe"
