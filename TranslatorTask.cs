using System.Net;
using System.Text.RegularExpressions;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;

public class TranslatorTask
{
    public class TaskData
    {
        public enum TaskState
        {
            Waiting,
            Processing,
            Completed,
            Failed,
            Closed,
        }
        public HttpListenerContext? context { get; set; }
        public string[] texts { get; set; }
        public string[] result { get; set; }
        public int reqID { get; set; }
        public int retryCount { get; set; }
        public TaskState state = TaskState.Waiting;

        //响应
        public bool TryRespond()
        {
            try
            {
                if (context == null || context.Response == null) return false;
                if (state == TaskState.Completed || state == TaskState.Failed)
                {
                    // 返回响应
                    string responseString = SimpleJson.SerializeTexts(result);
                    byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    context.Response.Close();
                    state = TaskState.Closed;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"响应失败: {ex.Message}");
            }            
            return false;
        }
    }


    private string[]? _apiKeys;
    private string? _model;
    private string? _requirement;
    private string? _url;
    private string? _terminology;
    private string? _gameName;
    private string? _gameDesc;
    private int _maxWordCount = 2000;
    private int _parallelCount = 10;
    private int _pollingInterval = 1000;
    private string? DestinationLanguage;
    private string? SourceLanguage;
    //使用半角符号
    private bool _halfWidth = true;
    private int _maxRetry = 10;
    private string _modelParams = "";
    List<TaskData> taskDatas = new List<TaskData>();
    TranslateDB translateDB = new TranslateDB();
    HttpListener listener = new HttpListener();
    //最近翻译
    List<string> recentTranslate = new List<string>();

    private int _currentKeyIndex = 0;
    public void Init(IInitializationContext context)
    {
        _apiKeys = context.GetOrCreateSetting("AutoLLM", "APIKey", "")?.Split(';') ?? new string[] { "NOKEY" };
        _model = context.GetOrCreateSetting("AutoLLM", "Model", "gpt-4o");
        _requirement = context.GetOrCreateSetting("AutoLLM", "Requirement", "");
        _url = context.GetOrCreateSetting("AutoLLM", "URL", "https://api.openai.com/v1/chat/completions");
        _terminology = context.GetOrCreateSetting("AutoLLM", "Terminology", "");
        _gameName = context.GetOrCreateSetting("AutoLLM", "GameName", "A Game");
        _gameDesc = context.GetOrCreateSetting("AutoLLM", "GameDesc", "");
        _maxWordCount = context.GetOrCreateSetting("AutoLLM", "MaxWordCount", 500);
        _parallelCount = context.GetOrCreateSetting("AutoLLM", "ParallelCount", 3);
        _pollingInterval = context.GetOrCreateSetting("AutoLLM", "Interval", 200);
        _halfWidth = context.GetOrCreateSetting("AutoLLM", "HalfWidth", true);
        _maxRetry = context.GetOrCreateSetting("AutoLLM", "MaxRetry", 10);
        _modelParams = context.GetOrCreateSetting("AutoLLM", "ModelParams", "");
        if (context.GetOrCreateSetting("AutoLLM", "DisableSpamChecks", false))
        {
            context.DisableSpamChecks();
        }

        if (_url.EndsWith("/v1"))
        {
            _url += "/chat/completions";
        }
        if (_url.EndsWith("/v1/"))
        {
            _url += "chat/completions";
        }

        DestinationLanguage = context.DestinationLanguage;
        SourceLanguage = context.SourceLanguage;
        if ((_apiKeys?.Length ?? 0) == 0 && !_url.Contains("localhost") && !_url.Contains("127.0.0.1") && !_url.Contains("192.168."))
        {
            throw new Exception("The AutoLLM endpoint requires an API key which has not been provided.");
        }
        translateDB.Init(context, _terminology);

        listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:20000/");
        // 启动监听
        listener.Start();
        Logger.Info("Listening for requests on http://127.0.0.1:20000/");


        // Start a separate thread for HTTP listener
        Thread listenerThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var ctx = listener.GetContext();
                    ProcessRequest(ctx);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"HTTP listener error: {ex.Message}");
            }
        });
        listenerThread.IsBackground = true;
        listenerThread.Start();

        Thread pollingThread = new Thread(Polling);
        pollingThread.IsBackground = true;
        pollingThread.Start();
    }

    private void ProcessRequest(HttpListenerContext context)
    {
        try
        {
            Logger.Debug($"处理请求: {context.Request.HttpMethod} {context.Request.Url}");
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            if (request.HttpMethod == "POST")
            {
                // 读取请求体
                using (Stream body = request.InputStream)
                using (StreamReader reader = new StreamReader(body, request.ContentEncoding))
                {
                    string requestBody = reader.ReadToEnd();
                    var texts = SimpleJson.ParseTexts(requestBody);
                    var task = AddTask(texts, context);
                }
            }
            if (request.HttpMethod == "GET")
            {
                // 处理 GET 请求
                string responseString = "AutoLLMTranslator is running.";
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"处理请求时发生错误: {ex.Message}");
            context.Response.Close();
        }
        // finally
        // {
        //     // 关闭响应
        //     context.Response.Close();
        // }
    }

    List<TaskData> SelectTasks()
    {
        var tasks = new List<TaskData>();
        int toltoken = 0;
        lock (_lockObject)
        {
            foreach (var task in taskDatas)
            {
                if (task.state == TaskData.TaskState.Waiting)
                {
                    int taskToken = 0;
                    foreach (var txt in task.texts)
                    {
                        taskToken += txt.Length;
                    }
                    if (toltoken + taskToken > _maxWordCount && tasks.Count > 0)
                    {
                        break;
                    }
                    if (task.retryCount > 2 && tasks.Count > 0)
                    {
                        break;
                    }
                    toltoken += taskToken;
                    tasks.Add(task);
                    if (task.retryCount > 0)//错过就单独处理
                        break;
                    //task.state = TaskData.TaskState.Processing;
                }
            }
        }
        return tasks;
    }

    public TaskData AddTask(string[] texts, HttpListenerContext context)
    {
        Logger.Debug($"添加任务: {string.Join(", ", texts)}");
        var task = new TaskData() { texts = texts, context = context };

        // 添加任务时上锁
        lock (_lockObject)
        {
            taskDatas.Insert(0, task);
        }

        // // 等待任务完成
        // task.WaitOne();

        // // 移除任务时上锁
        // lock (_lockObject)
        // {
        //     taskDatas.Remove(task);
        // }

        return task;
    }

    private string EscapeSpecialCharacters(string text)
    {
        //将换行进行转义
        text = text.Replace("\n", "\\n");
        text = text.Replace("\r", "\\r");
        text = text.Replace("\"", "<quote>");
        return text;
    }

    private string UnEscapeSpecialCharacters(string text)
    {
        //将换行进行转义
        text = text.Replace("\\n", "\n");
        text = text.Replace("\\r", "\r");
        text = text.Replace("<quote>", "\"");
        return text;
    }

    int curProcessingCount = 0;
    private readonly object _lockObject = new object();


    private string GetNextApiKey()
    {
        if (_apiKeys == null || _apiKeys.Length == 0)
        {
            return string.Empty;
        }
        lock (_apiKeys)
        {
            var key = _apiKeys[_currentKeyIndex];
            _currentKeyIndex = (_currentKeyIndex + 1) % _apiKeys.Length;
            return key;
        }
    }

    void TaskRespond(TaskData task)
    {
        task.TryRespond();
        lock (_lockObject)
        {
            taskDatas.Remove(task);
        }
    }
    void ProcessTaskBatch(List<TaskData> tasks)
    {
        int hashkey = tasks.GetHashCode();
        try
        {
            //Log($"翻译开始Batch:" + hashkey);
            foreach (var task in tasks)
            {
                Logger.Debug($"{hashkey} 翻译开始:{task.texts[0]}");
            }
            List<string> texts = new List<string>();
            foreach (var task in tasks)
            {
                texts.AddRange(task.texts);
            }
            var system = Config.prompt_base
            .Replace("{{GAMENAME}}", _gameName)
            .Replace("{{GAMEDESC}}", _gameDesc)
            .Replace("{{OTHER}}", _requirement)
            .Replace("{{HISTORY}}", string.Join("\n", translateDB.Search(texts, 1000).ToArray()))
            .Replace("{{TARGET_LAN}}", DestinationLanguage)
            .Replace("{{SOURCE_LAN}}", SourceLanguage)
            .Replace("{{RECENT}}", string.Join("\n", recentTranslate.ToArray()));
            var otxt = "";
            int index = 1;
            foreach (var data in texts)
            {
                var t = EscapeSpecialCharacters(data);
                otxt += $"[{index}]=\"{t}\"\n";
                index++;
            }
            // otxt += "]";
            if (system.Contains("/no_think") || system.Contains("/nothink"))
            {
                otxt = otxt + "\n/no_think";
            }
            var messages = new List<object>
            {
                new { role = "system", content = system },
                new { role = "user", content = otxt}
            };

            var requestBody = new Dictionary<string, object>
            {
                { "model", _model },
                { "temperature", 0.1 },
                { "max_tokens", 4000 },
                { "messages", messages }
            };
            if (!string.IsNullOrEmpty(_modelParams))
            {
                try
                {
                    var modelParamsData = SimpleJson.ParseModelParams(_modelParams);
                    foreach (var item in modelParamsData)
                        requestBody[item.Key] = item.Value;
                }
                catch (Exception ex)
                {
                    Logger.Error($"模型参数解析错误: {ex.Message}");
                }
            }



            // 创建HTTP请求
            var request = (HttpWebRequest)WebRequest.Create(_url);
            request.Method = "POST";
            request.Headers.Add("Authorization", $"Bearer {GetNextApiKey()}");
            request.ContentType = "application/json";

            // 写入请求体
            requestBody.Add("stream", true);
            var requestJson = SimpleJson.Serialize(requestBody);
            //Log($"请求: {requestJson}");
            using (var streamWriter = new StreamWriter(request.GetRequestStream()))
            {
                streamWriter.Write(requestJson);
            }

            // 获取响应
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                var lineResponse = "";
                var fullResponse = "";
                string line;
                var i = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data: ")) continue;

                    var data = line.Substring(6);
                    if (data == "[DONE]") break;

                    try
                    {
                        var content = SimpleJson.ParseSseContent(data);
                        if (!string.IsNullOrEmpty(content))
                        {
                            lineResponse += content;
                            //Log($"流: {content} ::: {lineResponse}");
                            fullResponse += content;
                            if (lineResponse.Contains("</think>"))
                                lineResponse = Regex.Replace(lineResponse, "<think>.*?</think>", "", RegexOptions.Singleline);
                            if (lineResponse.Contains("</context_think>"))
                                lineResponse = Regex.Replace(lineResponse, "<context_think>.*?</context_think>", "", RegexOptions.Singleline);
                            if (string.IsNullOrEmpty(lineResponse))
                                continue;
                            Logger.Debug($"{hashkey} 流0: |{lineResponse}|");
                            var lineResponseTxts = lineResponse.Split('\n');
                            int point = 0;
                            foreach (var txt in lineResponseTxts)
                            {
                                Logger.Debug($"{hashkey} 流1: |{txt}| len: {txt.Length}");
                                point += Math.Max(1, txt.Length);
                                var rs = txt.Trim();
                                if (rs.Length == 0 || rs.IndexOf('\"') < 0)
                                {
                                    continue;
                                }
                                if (rs.Count(c => c == '\"') < 2)
                                {
                                    continue;
                                }
                                if (rs.EndsWith("\""))
                                {
                                    Logger.Debug($"{hashkey} 流2: {rs}");
                                    var match = Regex.Match(rs, @"\[(\d+)\]=""(.*?)""");
                                    if (!match.Success)
                                    {
                                        throw new Exception($"翻译结果错误 1: {fullResponse}");
                                    }
                                    var num = -1;
                                    int.TryParse(match.Groups[1].Value, out num);
                                    if (num < 1 || num > tasks.Count)
                                    {
                                        throw new Exception($"翻译结果错误 2: {fullResponse}");
                                    }
                                    rs = match.Groups[2].Value;
                                    if (string.IsNullOrEmpty(rs))
                                    {
                                        throw new Exception($"翻译结果错误 3: {fullResponse}");
                                    }
                                    if (_halfWidth)
                                    {
                                        //将全角符号转换为半角符号
                                        rs = Regex.Replace(rs, @"[！＂＃＄％＆＇（）＊＋，－．／０１２３４５６７８９：；＜＝＞？＠［＼］＾＿｀｛｜｝～]", m => ((char)(m.Value[0] - 0xFEE0)).ToString());
                                    }
                                    rs = UnEscapeSpecialCharacters(rs);
                                    //Log($"流2: {lineResponse}");
                                    var task = tasks[num - 1];
                                    task.result = new string[] { translateDB.FindTerminology(task.texts[0]) ?? rs };
                                    task.state = TaskData.TaskState.Completed;
                                    TaskRespond(task);
                                    Logger.Debug($"{hashkey} 流OK: {rs}");
                                    lock (recentTranslate)
                                    {
                                        if (recentTranslate.Count > 10)
                                            recentTranslate.RemoveAt(0);
                                        recentTranslate.Add($"{task.texts[0]} === {task.result[0]}");
                                    }
                                    if (translateDB.AddData(task.texts[0], task.result[0]))
                                        translateDB.SortData();
                                    i++;
                                    var hlineResponse = lineResponse;
                                    lineResponse = lineResponse.Substring(point);
                                    Logger.Debug($"{hashkey} 流截取后: {lineResponse}|olen:{hlineResponse.Length} | point: {point}");
                                    point = 0;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"解析流响应出错: {ex.Message}");
                    }
                }

                Logger.Debug($"full流: {fullResponse}");
            }

        }
        catch (WebException ex)
        {
            Logger.Error($"翻译失败: {ex.Message}");
            if (ex.Response != null)
            {
                using (var errorResponse = (HttpWebResponse)ex.Response)
                {
                    using (var reader = new StreamReader(errorResponse.GetResponseStream()))
                    {
                        var errorText = reader.ReadToEnd();
                        Logger.Error($"服务器错误响应: {errorText}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"翻译失败: {ex.Message}");
        }
        finally
        {
            Logger.Debug($"翻译结束:" + hashkey);
            foreach (var task in tasks)
            {
                //失败了重新翻译
                if (task.state != TaskData.TaskState.Completed && task.state != TaskData.TaskState.Closed)
                {
                    task.retryCount++;
                    if (task.retryCount < _maxRetry)
                    {
                        Logger.Debug($"重新翻译:" + task.texts[0]);
                        task.state = TaskData.TaskState.Waiting;
                        task.result = null;
                    }
                    else
                    {
                        Logger.Error($"重试翻译依然失败，没救了:" + task.texts[0]);
                        task.state = TaskData.TaskState.Failed;
                        TaskRespond(task);
                    }
                }
            }
            lock (_lockObject)
                curProcessingCount--;
        }
    }

    //轮询
    public void Polling()
    {
        while (true)
        {
            try
            {

                Thread.Sleep(_pollingInterval);
                //if (curProcessingCount > 0)
                Logger.Debug($"Polling curProcessingCount: {curProcessingCount}/{_parallelCount} TASKS: {taskDatas.Count}");
                if (curProcessingCount >= _parallelCount)
                {
                    continue;
                }
                List<List<TaskData>> taskDatass = new List<List<TaskData>>();
                List<TaskData> tasks;
                lock (_lockObject)
                {
                    tasks = SelectTasks();
                    //Logger.Debug($"Polling SelectTasks: {tasks.Count}");
                    while (tasks.Count > 0 && curProcessingCount < _parallelCount)
                    {
                        curProcessingCount++;
                        foreach (var task in tasks)
                        {
                            task.state = TaskData.TaskState.Processing;
                        }
                        taskDatass.Add(tasks);
                        tasks = SelectTasks();
                    }
                }

                // 在锁外启动任务处理
                if (taskDatass.Count > 0)
                {
                    foreach (var tasklist in taskDatass)
                    {
                        var taskListCopy = new List<TaskData>(tasklist); // Create a copy for thread safety
                        Thread processingThread = new Thread(() => ProcessTaskBatch(taskListCopy));
                        processingThread.IsBackground = true;
                        processingThread.Start();
                    }
                }
                // Log("Polling End");
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
            }
        }
    }
}