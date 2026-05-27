# TelegaScan

Десктопное приложение (WPF, .NET) для экспорта чатов Telegram: сообщения, медиа и HTML-отчёт.

## Сборка

```powershell
dotnet build TelegaScan.csproj -c Release
```

Один исполняемый файл:

```powershell
.\publish-single-file.ps1
```

## Запуск

Нужны `api_id` и `api_hash` с [my.telegram.org](https://my.telegram.org). Сессия сохраняется локально в `%LocalAppData%\TelegaScan\`.
