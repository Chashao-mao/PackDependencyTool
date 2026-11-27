<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

type Page = "instances" | "logdoctor" | "conflicts";

type Instance = {
  id: string;
  name: string;
  path: string;
  resolved_path: string;
  game_version: string;
  loader: string;
  loader_version: string;
  install_stage: string;
  last_played?: string | null;
};

type LogIssue = {
  rule: string;
  severity: string;
  title: string;
  explanation: string;
  culprit: string;
  fix: string;
  evidence: string;
  count: number;
};

type LogNoise = { count: number; signature: string; sample: string };

type LogReport = {
  summary: string;
  total_lines: number;
  noise_lines: number;
  issues: LogIssue[];
  noise: LogNoise[];
};

type Conflict = {
  kind: string;
  title: string;
  detail: string;
  mods: string[];
  evidence: string;
};

const page = ref<Page>("instances");
const backendOk = ref(false);
const instances = ref<Instance[]>([]);
const selectedId = ref("");
const loading = ref(false);
const message = ref("");
const report = ref<LogReport | null>(null);
const conflicts = ref<Conflict[]>([]);
const conflictScan = ref(false);

const selected = computed(() => instances.value.find((item) => item.id === selectedId.value));
const nav = [
  { id: "instances" as Page, label: "实例", icon: "M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z" },
  { id: "logdoctor" as Page, label: "Log Doctor", icon: "M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1" },
  { id: "conflicts" as Page, label: "冲突", icon: "M5 21V4m0 1h11l-2 4 2 4H5" },
];
const current = computed(() => nav.find((item) => item.id === page.value)!);
const conflictGroups = computed(() => {
  const groups: Record<string, number> = {};
  for (const item of conflicts.value) groups[item.kind] = (groups[item.kind] ?? 0) + 1;
  return groups;
});
const appWindow = getCurrentWindow();

onMounted(() => {
  invoke<string>("ping")
    .then(() => (backendOk.value = true))
    .catch(() => (backendOk.value = false));
});

function minimizeWindow() { appWindow.minimize(); }
function toggleMaximizeWindow() { appWindow.toggleMaximize(); }
function closeWindow() { appWindow.close(); }

async function loadInstances() {
  loading.value = true;
  message.value = "";
  try {
    instances.value = await invoke<Instance[]>("list_instances");
    if (!selectedId.value && instances.value.length) selectedId.value = instances.value[0].id;
    message.value = instances.value.length ? "" : "Axolotl 没有返回实例。";
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

function selectInstance(id: string) {
  selectedId.value = id;
  report.value = null;
  conflicts.value = [];
  conflictScan.value = false;
}

function go(next: Page, id?: string) {
  if (id) selectInstance(id);
  page.value = next;
}

async function runLogDoctor() {
  if (!selectedId.value) await loadInstances();
  if (!selectedId.value) return;
  loading.value = true;
  message.value = "";
  try {
    report.value = await invoke<LogReport>("analyze_log", { instanceId: selectedId.value });
    message.value = "";
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function scanConflicts() {
  if (!selectedId.value) await loadInstances();
  if (!selectedId.value) return;
  loading.value = true;
  conflictScan.value = false;
  message.value = "";
  try {
    conflicts.value = await invoke<Conflict[]>("scan_conflicts", { instanceId: selectedId.value });
    conflictScan.value = true;
    message.value = conflicts.value.length ? "" : "没有发现字节码冲突。";
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function launch() {
  if (!selectedId.value) return;
  loading.value = true;
  try {
    await invoke("launch_instance", { instanceId: selectedId.value });
    message.value = "启动请求已提交。";
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function backup() {
  if (!selectedId.value) return;
  loading.value = true;
  try {
    await invoke("backup_instance", { instanceId: selectedId.value });
    message.value = "备份请求已提交。";
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

const kindLabel: Record<string, string> = {
  "access-transformer": "AT",
  mixin: "Mixin",
  "duplicate-class": "重复类",
};
</script>

<template>
  <div class="window">
    <header class="titlebar" data-tauri-drag-region>
      <div class="titlebar-brand" data-tauri-drag-region>
        <img src="/app.png" alt="" />
        <span>Axolotl Companion</span>
      </div>
      <div class="window-controls">
        <button title="最小化" @click="minimizeWindow">—</button>
        <button title="最大化" @click="toggleMaximizeWindow">□</button>
        <button title="关闭" @click="closeWindow">×</button>
      </div>
    </header>

    <div class="app">
      <aside class="rail">
        <div class="brand"><img src="/app.png" alt="" /></div>
        <button
          v-for="item in nav"
          :key="item.id"
          class="rail-button"
          :class="{ active: page === item.id }"
          :title="item.label"
          @click="page = item.id"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path :d="item.icon" /></svg>
        </button>
        <span class="rail-dot" :class="{ ok: backendOk }"></span>
      </aside>

      <main class="workspace">
        <section class="surface">
          <header class="header">
            <div class="header-title">
              <span class="header-icon">
                <svg viewBox="0 0 24 24"><path :d="current.icon" /></svg>
              </span>
              <div>
                <h1>{{ current.label }}</h1>
                <p v-if="selected">{{ selected.name }} · {{ selected.game_version }} · {{ selected.loader }}</p>
                <p v-else>选择 Axolotl 实例后开始</p>
              </div>
            </div>
            <div class="header-actions">
              <button v-if="page === 'instances'" class="button secondary" :disabled="loading" @click="loadInstances">
                {{ loading ? "读取中…" : "刷新" }}
              </button>
              <button v-if="page === 'logdoctor'" class="button primary" :disabled="loading || !selectedId" @click="runLogDoctor">
                {{ loading ? "分析中…" : "分析日志" }}
              </button>
              <button v-if="page === 'conflicts'" class="button primary" :disabled="loading || !selectedId" @click="scanConflicts">
                {{ loading ? "扫描中…" : "扫描冲突" }}
              </button>
            </div>
          </header>

          <div class="body">
            <p v-if="message" class="banner">{{ message }}</p>

            <template v-if="page === 'instances'">
              <div v-if="!instances.length" class="connect">
                <h2>连接 Axolotl 实例</h2>
                <p>第一次读取会在 Axolotl 里请求一次授权，之后会话复用。</p>
                <button class="button primary" :disabled="loading" @click="loadInstances">读取实例</button>
              </div>

              <template v-else>
                <div class="instance-list">
                  <button
                    v-for="item in instances"
                    :key="item.id"
                    class="instance-row"
                    :class="{ active: item.id === selectedId }"
                    @click="selectInstance(item.id)"
                  >
                    <div class="instance-name">
                      <strong>{{ item.name }}</strong>
                      <span>{{ item.game_version }} · {{ item.loader }} {{ item.loader_version }}</span>
                    </div>
                    <span class="tag">{{ item.install_stage }}</span>
                    <span v-if="item.id === selectedId" class="selected-mark">已选择</span>
                  </button>
                </div>

                <div v-if="selected" class="instance-detail">
                  <div>
                    <span class="label">实例目录</span>
                    <code>{{ selected.resolved_path }}</code>
                  </div>
                  <div class="detail-actions">
                    <button class="button secondary" :disabled="loading" @click="backup">备份</button>
                    <button class="button secondary" @click="go('logdoctor')">Log Doctor</button>
                    <button class="button secondary" @click="go('conflicts')">扫描冲突</button>
                    <button class="button primary" :disabled="loading" @click="launch">启动游戏</button>
                  </div>
                </div>
              </template>
            </template>

            <template v-else-if="page === 'logdoctor'">
              <div v-if="!selectedId" class="connect">
                <h2>先选择实例</h2>
                <button class="button primary" @click="go('instances')">去实例页</button>
              </div>
              <template v-else>
                <div v-if="!report" class="connect">
                  <h2>分析 {{ selected?.name }} 的 latest.log</h2>
                  <p>只读日志，不修改实例，也不会启动游戏。</p>
                  <button class="button primary" :disabled="loading" @click="runLogDoctor">开始分析</button>
                </div>

                <template v-else>
                  <div class="verdict">
                    <span class="label">诊断结论</span>
                    <h2>{{ report.issues.length ? report.issues[0].title : "没有发现明确错误" }}</h2>
                    <p>{{ report.summary }}</p>
                    <div class="chips">
                      <span class="tag">{{ report.total_lines }} 行日志</span>
                      <span class="tag">{{ report.issues.length }} 类问题</span>
                      <span class="tag">{{ report.noise_lines }} 行已隔离噪声</span>
                    </div>
                  </div>

                  <article v-for="issue in report.issues" :key="issue.rule" class="issue">
                    <div class="issue-head">
                      <span class="severity" :class="issue.severity">{{ issue.severity }}</span>
                      <h3>{{ issue.title }}</h3>
                      <span class="count">×{{ issue.count }}</span>
                      <span v-if="issue.culprit" class="source-chip">可能相关：{{ issue.culprit }}</span>
                    </div>
                    <div class="issue-grid">
                      <div>
                        <span class="label">发生了什么</span>
                        <p>{{ issue.explanation }}</p>
                      </div>
                      <div class="next">
                        <span class="label">下一步</span>
                        <p>{{ issue.fix }}</p>
                      </div>
                    </div>
                    <details>
                      <summary>原始证据</summary>
                      <pre>{{ issue.evidence }}</pre>
                    </details>
                  </article>

                  <details v-if="report.noise.length" class="noise">
                    <summary>已隔离 {{ report.noise_lines }} 行重复噪声</summary>
                    <div v-for="item in report.noise" :key="item.signature" class="noise-row">
                      <strong>{{ item.count }}×</strong>
                      <code>{{ item.signature }}</code>
                    </div>
                  </details>
                </template>
              </template>
            </template>

            <template v-else>
              <div v-if="!selectedId" class="connect">
                <h2>先选择实例</h2>
                <button class="button primary" @click="go('instances')">去实例页</button>
              </div>
              <template v-else>
                <div v-if="!conflictScan" class="connect">
                  <h2>扫描 {{ selected?.name }} 的 Jar / Mixin</h2>
                  <p>检查重复类、Mixin 目标重叠和访问级别不同的 Access Transformer。</p>
                  <button class="button primary" :disabled="loading" @click="scanConflicts">开始扫描</button>
                </div>

                <template v-else-if="conflicts.length">
                  <div class="verdict">
                    <span class="label">扫描结果</span>
                    <h2>{{ conflicts.length }} 个冲突</h2>
                    <div class="chips">
                      <span v-for="(count, kind) in conflictGroups" :key="kind" class="tag">
                        {{ kindLabel[kind] ?? kind }} {{ count }}
                      </span>
                    </div>
                  </div>

                  <article v-for="item in conflicts" :key="item.kind + item.evidence" class="issue">
                    <div class="issue-head">
                      <span class="severity warning">{{ kindLabel[item.kind] ?? item.kind }}</span>
                      <h3>{{ item.title }}</h3>
                      <span class="source-chip">涉及：{{ item.mods.join("、") }}</span>
                    </div>
                    <p>{{ item.detail }}</p>
                    <details>
                      <summary>证据</summary>
                      <pre>{{ item.evidence }}</pre>
                    </details>
                  </article>
                </template>

                <div v-else class="connect">
                  <h2>没有发现字节码冲突</h2>
                  <p>只检查了 JAR 中的真实 class 注解和 AT 行，不代表没有依赖或版本问题。</p>
                </div>
              </template>
            </template>
          </div>
        </section>
      </main>
    </div>
  </div>
</template>
