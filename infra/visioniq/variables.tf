variable "project_id" {
  type        = string
  default     = "gbr-mts-wgh-hkt-dev"
  description = "Approved sandbox GCP project."
  validation {
    condition     = var.project_id == "gbr-mts-wgh-hkt-dev"
    error_message = "VisionIQ infrastructure is restricted to the approved DEV project."
  }
}

variable "region" {
  type        = string
  default     = "europe-west3"
  description = "Cloud Run and Scheduler location."
  validation {
    condition     = var.region == "europe-west3"
    error_message = "VisionIQ Cloud Run and Scheduler use europe-west3."
  }
}

variable "pubsub_push_service_account" {
  type        = string
  default     = "visioniq-pubsub-push"
  description = "Preprovisioned OIDC push identity; must be approved by the cloud team."
}

variable "scheduler_service_account" {
  type        = string
  default     = "visioniq-scheduler"
  description = "Preprovisioned Scheduler identity; must be approved by the cloud team."
}
