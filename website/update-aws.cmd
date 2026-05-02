@echo off
setlocal
goto:main
REM Define reusable upload command as a label with one argument (%1)

:upload
aws s3 cp dist\%1 s3://eqlogparser.kizant.net/%1 --content-type "text/html; charset=utf-8" --cache-control "no-cache, no-store, must-revalidate" --acl public-read
goto :eof

:uploadcss
aws s3 cp dist\css\style.css s3://eqlogparser.kizant.net/css/style.css --content-type "text/css" --acl public-read
goto :eof

:main
REM === Upload files ===
call :upload releasenotes.html
call :upload index.html
call :upload documentation.html
call :upload policy.html
call :upload status.html
REM call :uploadcss 

REM aws s3 cp s3://eqlogparser-logs . --recursive
REM cat */* > all-logs.txt

endlocal
pause
