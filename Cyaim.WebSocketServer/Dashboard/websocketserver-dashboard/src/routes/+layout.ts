// SPA mode: the dashboard is fully client-rendered and served by the backend middleware
// (adapter-static fallback index.html). All pages poll live APIs, so no prerendering.
// SPA 模式：看板完全客户端渲染，由后端中间件提供 fallback index.html；页面轮询实时 API，不做预渲染。
export const ssr = false;
export const prerender = false;
