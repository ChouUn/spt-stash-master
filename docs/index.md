# Stash Master 文档目录

> Parent: [English](../README.md) / [简体中文](../README.zh-CN.md)

SPT 一键整理仓库 mod 的项目文档。

- [development.md](development.md)：开发进度、实现说明、诊断与验证记录
- [requirements.md](requirements.md)：用户原始需求与待澄清问题
- [arch.md](arch.md)：总体设计，模块边界、依赖方向与核心数据流
- [feats/tag-grammar.md](feats/tag-grammar.md)：tag 规则语法契约
- [feats/packing.md](feats/packing.md)：排布目标、收纳空间选择、锁与求解预算
- [feats/stacking.md](feats/stacking.md)：统一堆叠、跨容器归属与失败处理
- [plans/mvp.md](plans/mvp.md)：MVP 实施计划
- [plans/collection-optimization.md](plans/collection-optimization.md)：
  多网格收纳、阶段预算与堆叠规划优化实施进度
- [plans/category-grouping.md](plans/category-grouping.md)：类别占用行跨度的全局优化
- [plans/packing-identity.md](plans/packing-identity.md)：等价物品求解与原位身份映射
- [plans/weighted-grouping.md](plans/weighted-grouping.md)：类别目标去重与联合加权
- [plans/global-packing.md](plans/global-packing.md)：最终排布统一规划全部容器
- [plans/type-order-tuning.md](plans/type-order-tuning.md)：客制化排序的调优开放问题

## 调研报告

- [reports/sort-comparison.md](reports/sort-comparison.md)：
  同一散乱快照上本项目、原生与 Stash Management Helper 的离线排布对照
- [reports/weapon-sort-boundaries.md](reports/weapon-sort-boundaries.md)：
  三组武器箱排序图例、完整结果与回滚验证，以及原生与 SMH 的回退边界

- [reports/fold-and-lock-detection.md](reports/fold-and-lock-detection.md)：
  SPT 4.1 客户端里可折叠、Pin/Lock 与原生排序的判定来源
- [reports/cpsat-runtime-feasibility.md](reports/cpsat-runtime-feasibility.md)：
  CP-SAT / OR-Tools 能否在客户端或服务端运行，规模实验与替代方案
- [reports/organize-performance.md](reports/organize-performance.md)：
  层级聚合里程碑的真实耗时、固定快照预算对照与优化方向
- [reports/grid-budget-profile.md](reports/grid-budget-profile.md)：
  当前按网格计分实现的阶段耗时、搜索进展与缩短预算实验
- [reports/template-grouping.md](reports/template-grouping.md)：
  末端模板聚合修复、独立主题验证及既有存档回放的归因边界
