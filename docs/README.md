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
| [MAINTENANCE.md](MAINTENANCE.md) | 构建、测试、提交、发布约定与未验证清单 |

## 阅读须知

- design 目录下的内容是方向，不是承诺。排期以路线图为准，并随实现反馈调整。
- 已经实现的部分：设备发现与分组、插孔感知、端点事件通知、更新检查，以及重做后的界面和实时电平条。
- 其余章节描述的能力（场景识别、策略引擎、调优层、开放接口等）目前都没有实现。
