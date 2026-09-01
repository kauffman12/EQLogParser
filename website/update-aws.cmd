@echo off
setlocal
goto:main
REM Define reusable upload command as a label with one argument (%1)

:upload
aws s3 cp dist\%1 s3://eqlogparser.kizant.net/%1 --content-type "text/html; charset=utf-8" --cache-control "no-cache, no-store, must-revalidate" --acl public-read
goto :eof

REM The stylesheet is addressed as css/style.css?v=<CSS_VERSION> and the CloudFront cache
REM policy (CacheWithVQuery) keeps 'v' in the cache key, so every version is its own object
REM to the edge. That makes a one-year immutable lifetime safe: bumping CSS_VERSION in
REM build.py is all that is needed to invalidate the previous copy.
:uploadcss
aws s3 cp dist\css\style.css s3://eqlogparser.kizant.net/css/style.css --content-type "text/css" --cache-control "public, max-age=31536000, immutable" --acl public-read
goto :eof

REM Images and vendored scripts are NOT content-hashed or versioned, so they keep a 30 day
REM lifetime instead of immutable: replacing one under the same name would otherwise stay
REM invisible to returning visitors for a year. After swapping such a file in place run:
REM   aws cloudfront create-invalidation --distribution-id ENSZKSL31RP92 --paths "/img/*"
:uploadassets
aws s3 cp dist\%~1 s3://eqlogparser.kizant.net/%~1 --recursive --cache-control "public, max-age=2592000" --acl public-read --only-show-errors
goto :eof

:uploadicon
aws s3 cp dist\favicon.ico s3://eqlogparser.kizant.net/favicon.ico --cache-control "public, max-age=2592000" --acl public-read
goto :eof

:uploadxml
aws s3 cp dist\%1 s3://eqlogparser.kizant.net/%1 --content-type "application/xml; charset=utf-8" --cache-control "no-cache, no-store, must-revalidate" --acl public-read
goto :eof

:uploadtxt
aws s3 cp dist\%1 s3://eqlogparser.kizant.net/%1 --content-type "text/plain; charset=utf-8" --cache-control "no-cache, no-store, must-revalidate" --acl public-read
goto :eof

:main
REM === Upload files ===
call :upload releasenotes.html
call :upload index.html
call :upload getting-started.html
call :upload documentation.html
call :upload faq.html
call :upload policy.html
call :upload status.html
call :upload download.html
call :upload 404.html
call :uploadxml sitemap.xml
call :uploadxml feed.xml
call :uploadtxt robots.txt
call :uploadcss
call :uploadassets img
call :uploadassets assets
call :uploadicon

REM aws s3 cp s3://eqlogparser-logs . --recursive
REM cat */* > all-logs.txt

endlocal
pause
