dotnet build -f net9.0-android -t:Install -c Release

dotnet build -f net9.0-ios -r ios-arm64 -c Debug && xcrun devicectl device install app --device C8460DB6-C2BF-57D2-AD9B-B4527B5DAF84 bin/Debug/net9.0-ios/ios-arm64/AIAgentLocal.app

dotnet run -f net9.0-maccatalyst