using RDP_connector;
using Spectre.Console;
using System.Diagnostics;
using System.DirectoryServices.AccountManagement;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

internal class Program
{
    private const string DbFilename = "servers.json";
    private static List<ManagedComputer> _computers = [];
    private static readonly ManagedComputer BackMarker = new() { Host = "BACK_MARKER", Description = "Вернуться в главное меню" };

    private static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Console.Title = "RDP Connector Tool";
        LoadComputers();

        bool keepRunning = true;
        while (keepRunning)
        {
            AnsiConsole.Clear();

            // Красивый заголовок
            AnsiConsole.Write(
                new FigletText("RDP connector")
                    .Centered()
                    .Color(Color.Blue));

            AnsiConsole.MarkupLine("[yellow]Убедитесь, что утилита запущена от имени Администратора.[/]\n");

            // Главное меню выбора действий
            string action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold blue]Выберите действие:[/]")
                    .PageSize(10)
                    .AddChoices([
                            "1. Найти компьютер по имени пользователя",
                            "2. Подключиться к компьютеру из списка",
                            "3. Быстрое подключение (ввод вручную)",
                            "4. Добавить компьютер в список",
                            "5. Редактировать компьютер",
                            "6. Удалить компьютер из списка",
                            "[yellow]Выход[/]"
                    ]));

            switch (action)
            {
                case "1. Найти компьютер по имени пользователя":
                    await GetComputersByUsername();
                    break;
                case "2. Подключиться к компьютеру из списка":
                    await ConnectFromListAsync();
                    break;
                case "3. Быстрое подключение (ввод вручную)":
                    string host = AnsiConsole.Ask<string>("Введите [green]имя компьютера[/] или [green]IP[/] (или [yellow]exit[/] для отмены):").Trim();
                    if (host.Equals("exit", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(host))
                        break;
                    await RunWorkflowAsync(host);
                    break;
                case "4. Добавить компьютер в список":
                    AddNewComputer();
                    break;
                case "5. Редактировать компьютер":
                    EditComputer();
                    break;
                case "6. Удалить компьютер из списка":
                    DeleteComputer();
                    break;
                case "[yellow]Выход[/]":
                    keepRunning = false;
                    break;
            }
        }
    }

    private static async Task RunWorkflowAsync(string targetComputer)
    {
        AnsiConsole.WriteLine();

        // 1. Проверка доступности (Ping)
        bool isAlive = await AnsiConsole.Status().StartAsync("Проверка доступности узла (Ping)...", async ctx => await CheckPingAsync(targetComputer));

        if (!isAlive)
        {
            AnsiConsole.MarkupLine($"[red]Компьютер {targetComputer} недоступен по сети (Ping failed).[/]");
            WaitForExit();
            return;
        }

        AnsiConsole.MarkupLine($"[green]Компьютер {targetComputer} доступен по сети.[/]\n");

        // 2. Получение списка сессий через qwinsta
        Table? sessionsTable = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Получение списка активных сессий...", async ctx => await GetRemoteSessionsAsync(targetComputer));

        if (sessionsTable == null || sessionsTable.Rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Не удалось получить список сессий или активных пользователей нет.[/]");
            WaitForExit();
            return;
        }

        // 3. Вывод таблицы сессий
        AnsiConsole.Write(sessionsTable);

        // 4. Выбор ID сессии для подключения
        string sessionId = AnsiConsole.Ask<string>("Введите [green]ID сессии[/] для теневого подключения (или [yellow]exit[/] для отмены):").Trim();
        if (sessionId.Equals("exit", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(sessionId))
        {
            AnsiConsole.MarkupLine("[yellow]Подключение отменено.[/]");
            Thread.Sleep(1000); // Короткая пауза, чтобы пользователь заметил статус отмены
            return;
        }

        // 5. Выбор режима подключения
        string mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold blue]Выберите режим теневого подключения[/]:")
                .PageSize(4)
                .AddChoices([
                        "Наблюдение (Только просмотр)",
                        "Управление (Интерактивный режим)",
                        "[yellow]<- Назад (Отмена подключения)[/]"
                ]));

        if (mode.Contains("Назад"))
        {
            AnsiConsole.MarkupLine("[yellow]Подключение отменено.[/]");
            Thread.Sleep(1000);
            return;
        }

        bool controlMode = mode.Contains("Управление");

        // 6. Запуск RDP Shadow
        AnsiConsole.MarkupLine($"[yellow]Запуск теневого подключения к сессии {sessionId}...[/]");
        LaunchRdpShadow(targetComputer, sessionId, controlMode);

        WaitForExit();
    }

    private static async Task ConnectFromListAsync()
    {
        if (_computers.Count == 0)
        {
            AnsiConsole.MarkupLine("Список сохраненных компьютеров [yellow]пуст[/]. Сначала [yellow]добавьте компьютер[/].");
            WaitForExit();
            return;
        }

        ManagedComputer selected = AnsiConsole.Prompt(BuildComputerList());

        if (selected == BackMarker)
            return;

        await RunWorkflowAsync(selected.Host);
    }

    private static void AddNewComputer()
    {
        string host = AnsiConsole.Ask<string>("Введите [green]имя компьютера[/] или [green]IP[/] (или [yellow]exit[/] для отмены):").Trim();
        if (host.Equals("exit", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(host))
            return;
        string desc = AnsiConsole.Ask<string>("Введите [blue]описание/подпись[/], например, 'Кошерица' (или [yellow]exit[/] для отмены):").Trim();
        if (desc.Equals("exit", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(host))
            return;

        if (string.IsNullOrEmpty(host))
            return;
        if (_computers.Any(item => item.Host == host))
        {
            AnsiConsole.MarkupLine("В списке [yellow]уже есть[/] компьютер с таким [yellow]именем[/] или [yellow]IP[/]!");
            Thread.Sleep(1000);
            return;
        }

        _computers.Add(new ManagedComputer { Host = host, Description = desc });
        SaveComputers();
        AnsiConsole.MarkupLine("[green]Компьютер успешно добавлен в список![/]");
        WaitForExit();
    }

    private static void EditComputer()
    {
        if (_computers.Count == 0)
        {
            AnsiConsole.MarkupLine("Список [yellow]пуст[/], нечего редактировать.");
            WaitForExit();
            return;
        }

        ManagedComputer selected = AnsiConsole.Prompt(BuildComputerList("Выберите компьютер для [yellow]редактирования[/]:"));

        if (selected == BackMarker)
            return;

        AnsiConsole.MarkupLine($"\nРедактирование записи для [yellow]{selected.Host}[/]. Нажмите [yellow]Enter[/], чтобы оставить без изменений.");

        // Запрашиваем новые данные, подставляя старые значения по умолчанию
        string newHost = AnsiConsole.Prompt(
            new TextPrompt<string>("Новое [green]имя компьютера[/] или [green]IP[/]:")
                .DefaultValue(selected.Host)).Trim();

        string newDesc = AnsiConsole.Prompt(
            new TextPrompt<string>("Новое [blue]описание/подпись[/]:")
                .DefaultValue(selected.Description)).Trim();

        if (selected.Host != newHost && _computers.Any(item => item.Host == newHost))
        {
            AnsiConsole.MarkupLine("В списке [yellow]уже есть[/] компьютер с таким [yellow]именем[/] или [yellow]IP[/]!");
            Thread.Sleep(1000);
            return;
        }

        // Обновляем данные в выбранном объекте
        selected.Host = newHost;
        selected.Description = newDesc;

        SaveComputers();
        AnsiConsole.MarkupLine("[green]Запись успешно обновлена![/]");
        WaitForExit();
    }

    private static void DeleteComputer()
    {
        if (_computers.Count == 0)
        {
            AnsiConsole.MarkupLine("Список [yellow]пуст[/], нечего удалять.");
            WaitForExit();
            return;
        }

        ManagedComputer selected = AnsiConsole.Prompt(BuildComputerList("Выберите компьютер для [red]удаления[/] ([grey]начните вводить текст для поиска[/]):"));

        if (selected == BackMarker)
            return;

        if (AnsiConsole.Confirm($"Вы уверены, что хотите [red]удалить[/] {selected.Host}?"))
        {
            _computers.Remove(selected);
            SaveComputers();
            AnsiConsole.MarkupLine("[green]Удалено.[/]");
        }
        WaitForExit();
    }

    // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ (СЕТЬ, РАБОТА С ФАЙЛАМИ, РАБОТА С ПРОЦЕССАМИ)
    private static async Task<bool> CheckPingAsync(string host)
    {
        try
        {
            using Ping ping = new();
            PingReply reply = await ping.SendPingAsync(host, 2000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Table?> GetRemoteSessionsAsync(string host)
    {
        return await Task.Run(() =>
        {
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = "qwinsta",
                    Arguments = $"/server:{host}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using Process? process = Process.Start(startInfo);
                if (process == null)
                    return null;

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                Table table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("[blue]СЕАНС[/]");
                table.AddColumn("[blue]ПОЛЬЗОВАТЕЛЬ[/]");
                table.AddColumn("[blue]ID[/]");
                table.AddColumn("[blue]СТАТУС[/]");
                table.AddColumn("[blue]ТИП[/]");
                table.AddColumn("[blue]УСТР-ВО[/]");

                string[] lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
                int[] startIndexes =
                [
                    lines[0].IndexOf("СЕАНС"),
                    lines[0].IndexOf("ПОЛЬЗОВАТЕЛЬ"),
                    lines[0].IndexOf("ID"),
                    lines[0].IndexOf("СТАТУС"),
                    lines[0].IndexOf("ТИП"),
                    lines[0].IndexOf("УСТР-ВО"),
                ];

                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    string sessionName = line[startIndexes[0]..startIndexes[1]].Trim();
                    string username = line[startIndexes[1]..startIndexes[2]].Trim();
                    string id = line[startIndexes[2]..startIndexes[3]].Trim();
                    string state = line[startIndexes[3]..startIndexes[4]].Trim();
                    string type = line[startIndexes[4]..startIndexes[5]].Trim();
                    string device = line[startIndexes[5]..].Trim();
                    if (state.Equals("Активно", StringComparison.OrdinalIgnoreCase) || state.Equals("Active", StringComparison.OrdinalIgnoreCase))
                    {
                        state = $"[green]{state}[/]";
                        username = $"[green]{username}[/]";
                    }
                    table.AddRow(sessionName, username, id, state, type, device);
                }
                return table;
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                return null;
            }
        });
    }
    private static void LaunchRdpShadow(string host, string sessionId, bool control)
    {
        try
        {
            string arguments = $"/shadow:{sessionId} /v:{host}";
            if (control)
                arguments += " /control";
            ProcessStartInfo startInfo = new()
            {
                FileName = "mstsc",
                Arguments = arguments,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            AnsiConsole.MarkupLine("[green]Команда подключения успешно отправлена.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Ошибка запуска mstsc:[/]");
            AnsiConsole.WriteException(ex);
        }
    }
    private static void LoadComputers()
    {
        try
        {
            if (File.Exists(DbFilename))
            {
                string json = File.ReadAllText(DbFilename);
                _computers = JsonSerializer.Deserialize<List<ManagedComputer>>(json) ?? [];
                _computers = [.. _computers.OrderBy(item => item.Description)];
            }
        }
        catch
        {
            _computers = [];
        }
    }
    private static void SaveComputers()
    {
        try
        {
            string json = JsonSerializer.Serialize(_computers, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(DbFilename, json);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Ошибка сохранения базы данных:[/]");
            AnsiConsole.WriteException(ex);
        }
    }
    private static void WaitForExit()
    {
        AnsiConsole.MarkupLine("\n[grey]Нажмите любую клавишу, чтобы вернуться в меню...[/]");
        Console.ReadKey(true);
    }

    private static SelectionPrompt<ManagedComputer> BuildComputerList(string title = "Выберите компьютер из списка ([grey]начните вводить текст для поиска[/]):")
    {
        List<ManagedComputer> choices = [BackMarker, .. _computers];

        return new SelectionPrompt<ManagedComputer>()
                .Title(title)
                .PageSize(15)
                .EnableSearch()
                .SearchPlaceholderText("[grey]Введите имя или описание для фильтрации...[/]")
                .UseConverter(c => c.Host == "BACK_MARKER" ? $"[yellow]<- Назад (Отмена)[/]" : c.ToString())
                .AddChoices(choices);
    }

    private static async Task GetComputersByUsername()
    {
        string username = AnsiConsole.Ask<string>("Введите [green]имя пользователя[/] (или [yellow]exit[/] для отмены):").Trim();
        if (username.Equals("exit", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(username))
            return;

        List<ManagedComputer>? computers = await GetComputersByUsername(username);
        if (computers == null)
        {
            AnsiConsole.MarkupLine("Компьютеры с таким пользователем [yellow]не найдены[/].");
            Thread.Sleep(1000);
            return;
        }
        List<ManagedComputer> choices = [BackMarker, .. computers];

        ManagedComputer selected = AnsiConsole.Prompt(new SelectionPrompt<ManagedComputer>().Title("Выберите компьютер из списка ([grey]начните вводить текст для поиска[/]):")
                                                                                            .PageSize(15)
                                                                                            .EnableSearch()
                                                                                            .SearchPlaceholderText("[grey]Введите имя или описание для фильтрации...[/]")
                                                                                            .UseConverter(c => c.Host == "BACK_MARKER" ? $"[yellow]<- Назад (Отмена)[/]" : c.ToString())
                                                                                            .AddChoices(choices));

        if (selected == BackMarker)
            return;

        await RunWorkflowAsync(selected.Host);
    }

    private static async Task<List<ManagedComputer>?> GetComputersByUsername(string username)
    {
        return await Task.Run(() =>
        {
            try
            {
                using PrincipalContext context = new(ContextType.Domain);
                using ComputerPrincipal computerTemplate = new(context);
                computerTemplate.Description = $"*{username}*";

                using PrincipalSearcher searcher = new(computerTemplate);
                PrincipalSearchResult<Principal> results = searcher.FindAll();
                List<ManagedComputer> computers = [];

                foreach (ComputerPrincipal pc in results.Cast<ComputerPrincipal>())
                {
                    computers.Add(new()
                    {
                        Host = pc.Name,
                        Description = pc.Description
                    });
                }

                if (computers.Count == 0)
                    return null;
                return computers;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Ошибка при обращении к Active Directory:[/]");
                AnsiConsole.WriteException(ex);
                return null;
            }
        });
    }
}
