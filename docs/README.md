# DeskAudio 文档

本目录记录 DeskAudio 的设计方向和维护约定。写给这个工程的维护者和后续贡献者。

| 文档 | 内容 |
| --- | --- |
| [design/00-overview.md](design/00-overview.md) | 目标、原则、总体架构 |
| [design/01-perception-cognition.md](design/01-perception-cognition.md) | 感知层与认知层 |
| [design/02-decision-action.md](design/02-decision-action.md) | 策略引擎与动作层 |
| [design/03-tuning.md](design/03-tuning.md) | 声音调优 |
| [design/04-interaction-api.md](design/04-interaction-api.md) | 交互层与开放接口 |
| [design/05-data-security.md](design/05-data-security.md) | 数据、隐私、安全，以及与现有模块的映射 |
| [design/06-roadmap.md](design/06-roadmap.md) | 路线图、验收指标、风险与远景 |
| [design/07-algorithm-brief.md](design/07-algorithm-brief.md) | 后台算法交接方案 |
| [design/08-algorithm-implementation.md](design/08-algorithm-implementation.md) | 开发版实现参数与未测指标 |
| [MAINTENANCE.md](MAINTENANCE.md) | 构建、测试、提交、发布约定与未验证清单 |

## 阅读须知

- design 目录下的内容是方向，不是承诺。排期以路线图为准，并随实现反馈调整。
- v1.1 已实现 P1 的后台扫描与去抖、端点事件总线、决策记录与撤销、屏幕提示和版本化配置。测试命令、真机验证范围和发布约定见 [维护文档](MAINTENANCE.md)。
- v1.3 已实现 P2 规则场景识别、评分与迟滞、四级自治、会议与蓝牙规避和活动时间线。v1.4 实现 P3 等响匹配、柔着陆、麦克风医生和 EQ 推荐。P4 与 P5 涵盖开放接口和驱动能力。
