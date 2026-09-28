using System;
using System.Collections.Generic;

namespace CampusNetHelper
{
    /// <summary>
    /// 结构化体检单项。UI 层以统一徽章卡片渲染。
    /// </summary>
    public class HealthCheckStep
    {
        public int StepNumber { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }      // "OK", "ERROR", "WARN", "INFO"
        public string StatusBadge { get; set; } // "✔ 正常", "✘ 异常", "⚠ 提示"
        public string Summary { get; set; }
        public List<string> Details { get; set; }

        public HealthCheckStep()
        {
            Details = new List<string>();
        }
    }
}
